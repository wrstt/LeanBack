namespace LeanBack.Engine;

/// <summary>
/// Walks a project tree and flags directories that are reinstallable per the preset/marker
/// table. Flagged dirs are sized but never descended into for further flagging. Reparse
/// points (symlinks/junctions) are skipped entirely. .git is sized separately.
/// </summary>
public static class Scanner
{
    public static ScanResult Scan(string root, Action<int, long>? progress, CancellationToken ct)
    {
        var result = new ScanResult
        {
            Path = root,
            Name = System.IO.Path.GetFileName(root.TrimEnd('\\', '/')),
        };

        var rootDi = new DirectoryInfo(root);
        if (!rootDi.Exists) throw new DirectoryNotFoundException(root);

        result.Kind = DetectKind(rootDi);

        int seenFiles = 0;
        long seenBytes = 0;
        long lastReport = 0;
        var cmds = new List<RegenCmd>();
        var cmdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Report()
        {
            if (progress == null) return;
            long now = Environment.TickCount64;
            if (now - lastReport > 120)
            {
                lastReport = now;
                progress(seenFiles, seenBytes);
            }
        }

        void AddCmd(string cmd, string brings)
        {
            if (cmdSet.Add(cmd)) cmds.Add(new RegenCmd { Cmd = cmd, Brings = brings });
            else
            {
                var existing = cmds.First(c => c.Cmd.Equals(cmd, StringComparison.OrdinalIgnoreCase));
                foreach (var b in brings.Split(", "))
                    if (!existing.Brings.Contains(b)) existing.Brings += ", " + b;
            }
        }

        // ---- top level listing (for dry run preview) ----
        foreach (var e in rootDi.EnumerateFileSystemInfos())
            result.TopLevel.Add(new TopEntry { Name = e.Name, IsDir = e is DirectoryInfo });
        result.TopLevel.Sort((a, b) =>
            a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        // ---- main walk ----
        void Walk(DirectoryInfo dir, string rel)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos(); }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                if (entry is FileInfo fi)
                {
                    result.TotalFiles++; result.KeptFiles++;
                    result.TotalBytes += fi.Length; result.KeptBytes += fi.Length;
                    seenFiles++; seenBytes += fi.Length;
                    Report();
                    continue;
                }

                var sub = (DirectoryInfo)entry;
                string subRel = rel.Length == 0 ? sub.Name : rel + "\\" + sub.Name;

                if (sub.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    var (b, f) = SizeOf(sub, ct, ref seenFiles, ref seenBytes, Report);
                    result.GitBytes += b;
                    result.TotalBytes += b;
                    result.TotalFiles += f;
                    if (rel.Length == 0) result.HasGit = true;
                    continue;
                }

                var preset = MatchPreset(sub, dir);
                if (preset != null)
                {
                    var (b, f) = SizeOf(sub, ct, ref seenFiles, ref seenBytes, Report);
                    result.Rows.Add(new FlaggedDir
                    {
                        Rel = subRel, Bytes = b, Files = f,
                        Reason = preset.Reason, DefaultChecked = preset.DefaultChecked,
                    });
                    result.TotalBytes += b;
                    result.TotalFiles += f;
                    if (preset.Cmd != null) AddCmd(preset.Cmd, preset.Brings ?? subRel);
                    continue;
                }

                Walk(sub, subRel);
            }
        }

        Walk(rootDi, "");

        // ---- .gitignore hint layer: gitignored root dirs > 50 MB not already flagged ----
        try { AddGitignoreHints(rootDi, result, ct); } catch { /* hints are best-effort */ }

        // Largest first, hints (unchecked-by-default) after preset rows of same order
        result.Rows = result.Rows
            .OrderByDescending(r => r.Reason != "gitignored")
            .ThenByDescending(r => r.Bytes)
            .ToList();
        result.RegenCmds = cmds;
        progress?.Invoke(seenFiles, seenBytes);
        return result;
    }

    private sealed record Preset(string Reason, bool DefaultChecked, string? Cmd, string? Brings);

    private static Preset? MatchPreset(DirectoryInfo dir, DirectoryInfo parent)
    {
        string n = dir.Name.ToLowerInvariant();

        bool Has(string file) => File.Exists(Path.Combine(parent.FullName, file));
        bool HasGlob(string glob)
        {
            try { return parent.EnumerateFiles(glob).Any(); } catch { return false; }
        }

        switch (n)
        {
            case "node_modules" when Has("package.json"):
                return new Preset("npm install", true, NodeInstallCmd(parent), "node_modules/");

            case ".next" or ".output" when Has("package.json"):
                return new Preset("build output", true, "npm run build", dir.Name + "/");
            case "dist" or "out" when Has("package.json"):
                return new Preset("build output", true, "npm run build", dir.Name + "/");
            case ".turbo" or ".cache" or ".parcel-cache" or ".vite" when Has("package.json"):
                return new Preset("cache", true, null, null);

            case "build" when Has("package.json"):
                return new Preset("build output", true, "npm run build", "build/");
            case "build" when HasGlob("build.gradle*"):
                return new Preset("gradle build", true, "gradlew build", "build/");
            case ".gradle" when HasGlob("build.gradle*"):
                return new Preset("gradle build", true, "gradlew build", ".gradle/");

            case ".venv" or "venv" or "env" when File.Exists(Path.Combine(dir.FullName, "pyvenv.cfg")):
                return new Preset("pip install", true,
                    "python -m venv " + dir.Name + " && pip install -r requirements.txt", dir.Name + "/");

            case "__pycache__" or ".pytest_cache" or ".mypy_cache" or ".ruff_cache":
                return new Preset("auto-generated", true, null, null);

            case "target" when Has("Cargo.toml"):
                return new Preset("cargo build", true, "cargo build", "target/");

            case "bin" or "obj" when HasGlob("*.csproj") || HasGlob("*.sln"):
                return new Preset("dotnet build", true, "dotnet build", "bin/, obj/");

            case "vendor" when Has("composer.json"):
                return new Preset("composer install", true, "composer install", "vendor/");
            case "vendor" when Has("go.mod"):
                // Go vendor may be intentional — surface it but default UNCHECKED
                return new Preset("go vendor", false, "go mod vendor", "vendor/");

            case "pods" when Has("Podfile"):
                return new Preset("pod install", true, "pod install", "Pods/");

            case ".terraform" when HasGlob("*.tf"):
                return new Preset("terraform init", true, "terraform init", ".terraform/");

            case "coverage" or ".nyc_output" or ".tox" or ".nox":
                return new Preset("test artifacts", true, null, null);

            default:
                if (n.EndsWith(".egg-info")) return new Preset("auto-generated", true, null, null);
                return null;
        }
    }

    private static string NodeInstallCmd(DirectoryInfo parent)
    {
        if (File.Exists(Path.Combine(parent.FullName, "pnpm-lock.yaml"))) return "pnpm install";
        if (File.Exists(Path.Combine(parent.FullName, "yarn.lock"))) return "yarn install";
        if (File.Exists(Path.Combine(parent.FullName, "bun.lockb")) ||
            File.Exists(Path.Combine(parent.FullName, "bun.lock"))) return "bun install";
        return "npm install";
    }

    private static string DetectKind(DirectoryInfo root)
    {
        bool Has(string f) => File.Exists(Path.Combine(root.FullName, f));
        bool HasGlob(string g) { try { return root.EnumerateFiles(g).Any(); } catch { return false; } }

        if (Has("Cargo.toml")) return "Rust";
        if (Has("package.json"))
        {
            try
            {
                var pkg = File.ReadAllText(Path.Combine(root.FullName, "package.json"));
                if (pkg.Contains("\"next\"")) return "Node / Next.js";
                if (pkg.Contains("\"vite\"")) return "Node / Vite";
                if (pkg.Contains("\"react\"")) return "Node / React";
            }
            catch { }
            return "Node";
        }
        if (Has("pyproject.toml") || Has("requirements.txt") || Has("setup.py")) return "Python";
        if (Has("go.mod")) return "Go";
        if (HasGlob("*.sln") || HasGlob("*.csproj")) return ".NET";
        if (HasGlob("build.gradle*")) return "Gradle / JVM";
        if (Has("composer.json")) return "PHP";
        return "Folder";
    }

    private static (long bytes, int files) SizeOf(DirectoryInfo dir, CancellationToken ct,
        ref int seenFiles, ref long seenBytes, Action report)
    {
        long bytes = 0; int files = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = d.EnumerateFileSystemInfos(); }
            catch { continue; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is FileInfo fi) { bytes += fi.Length; files++; seenFiles++; seenBytes += fi.Length; }
                else stack.Push((DirectoryInfo)e);
            }
            report();
        }
        return (bytes, files);
    }

    private static void AddGitignoreHints(DirectoryInfo root, ScanResult result, CancellationToken ct)
    {
        string gi = Path.Combine(root.FullName, ".gitignore");
        if (!File.Exists(gi)) return;
        var flagged = new HashSet<string>(result.Rows.Select(r => r.Rel), StringComparer.OrdinalIgnoreCase);
        const long minBytes = 50L * 1024 * 1024;

        foreach (var raw in File.ReadAllLines(gi))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
            line = line.TrimStart('/').TrimEnd('/');
            if (line.Length == 0 || line.Contains('*') || line.Contains('/') || line.Contains('\\')) continue;
            if (line.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            if (flagged.Contains(line)) continue;

            var dir = new DirectoryInfo(Path.Combine(root.FullName, line));
            if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            int sf = 0; long sb = 0;
            var (bytes, files) = SizeOf(dir, ct, ref sf, ref sb, static () => { });
            if (bytes < minBytes) continue;

            result.Rows.Add(new FlaggedDir
            {
                Rel = line, Bytes = bytes, Files = files,
                Reason = "gitignored", DefaultChecked = false,
            });
            flagged.Add(line);
        }
    }
}
