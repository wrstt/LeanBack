using System.IO.Compression;
using System.IO.Hashing;
using System.Text.Json;

namespace LeanBack.Engine;

/// <summary>
/// Mirror / zip backup with pruned enumeration (excluded dirs are never descended into),
/// staged writes (.partial → verify → rename → delete old), verification and keep-1 retention.
/// </summary>
public static class BackupEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public const string AppVersion = "1.0.0";

    // ------------------------------------------------------------------ drives

    public static List<DriveEntry> ListDrives()
    {
        var list = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is DriveType.CDRom or DriveType.Ram or DriveType.Unknown) continue;
                string type = d.DriveType switch
                {
                    DriveType.Removable => "USB / Removable",
                    DriveType.Network => "Network share",
                    _ => "Local / Fixed",
                };
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? type : d.VolumeLabel + " · " + type;
                list.Add(new DriveEntry
                {
                    Root = d.Name,                       // "E:\"
                    Type = type,
                    FreeBytes = d.AvailableFreeSpace,
                    Display = d.Name + " — " + label + " (" + Fmt(d.AvailableFreeSpace) + " free)",
                });
            }
            catch { /* drive vanished mid-enumeration */ }
        }
        // Removable drives first — that's what you back up to
        return list
            .OrderByDescending(x => x.Type == "USB / Removable")
            .ThenByDescending(x => x.Type == "Local / Fixed" && !x.Root.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Root)
            .ToList();
    }

    public static string Fmt(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? (mb / 1024).ToString("0.00") + " GB" : Math.Round(Math.Max(mb, bytes > 0 ? 1 : 0)) + " MB";
    }

    // ------------------------------------------------------------------ backup

    public static BackupOutcome Run(BackupRequest req, ProgressFn progress, CancellationToken ct)
    {
        string projName = Path.GetFileName(req.Path.TrimEnd('\\', '/'));
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        string baseName = projName + "_" + stamp;
        string destDir = Path.Combine(req.Dest, "LeanBack");
        bool zip = req.Format.Equals("zip", StringComparison.OrdinalIgnoreCase);
        string finalName = baseName + (zip ? ".zip" : "");
        string finalPath = Path.Combine(destDir, finalName);
        string stagePath = finalPath + ".partial";

        var outcome = new BackupOutcome { Name = finalName, BackupPath = finalPath };
        try
        {
            // 1. enumerate with pruning (never walks into excluded dirs)
            progress("scan", 0, 0, 0, 0, "");
            var files = EnumerateFiles(req, ct, out long totalBytes);
            progress("scan", files.Count, files.Count, 0, totalBytes, "");

            // free-space check before writing anything (best-effort: UNC roots can't be probed)
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destDir))!);
                if (drive.AvailableFreeSpace < totalBytes + 64L * 1024 * 1024)
                {
                    outcome.Error = "Not enough space on " + req.Dest + " — backup needs " +
                        Fmt(totalBytes) + ", drive has " + Fmt(drive.AvailableFreeSpace) + " free";
                    return outcome;
                }
            }
            catch (ArgumentException) { }

            Directory.CreateDirectory(destDir);
            CleanupStale(destDir, projName); // stale .partial from a previous crash

            string manifest = BuildManifest(req, projName, files.Count, totalBytes);

            // 2. copy / zip to staging
            if (zip) WriteZip(req.Path, stagePath, files, manifest, progress, totalBytes, ct);
            else WriteMirror(req.Path, stagePath, files, manifest, progress, totalBytes, ct);

            // 3. verify staged backup against source
            string? verifyError = zip
                ? VerifyZip(req.Path, stagePath, files, progress, totalBytes, ct)
                : VerifyMirror(req.Path, stagePath, files, progress, totalBytes, ct);
            if (verifyError != null)
            {
                TryDelete(stagePath, zip);
                outcome.Error = "Verification failed — nothing was replaced. " + verifyError;
                return outcome;
            }

            // 4. commit: rename staging → final, then (and only then) delete old backups.
            // A same-minute rerun produces the same name — the verified new copy wins.
            if (zip)
            {
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(stagePath, finalPath);
            }
            else
            {
                if (Directory.Exists(finalPath)) Directory.Delete(finalPath, true);
                Directory.Move(stagePath, finalPath);
            }
            DeleteOldBackups(destDir, projName, finalName);

            outcome.Ok = true;
            outcome.Files = files.Count;
            outcome.Bytes = totalBytes;
            return outcome;
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagePath, zip);
            outcome.Cancelled = true;
            return outcome;
        }
        catch (Exception ex)
        {
            TryDelete(stagePath, zip);
            outcome.Error = ex.Message;
            return outcome;
        }
    }

    // Dirs are pruned before descending — a 50 GB node_modules is never walked.
    private static List<(string Rel, long Len, DateTime MtimeUtc)> EnumerateFiles(
        BackupRequest req, CancellationToken ct, out long totalBytes)
    {
        var exclude = new HashSet<string>(req.Exclude, StringComparer.OrdinalIgnoreCase);
        var (dirPatterns, filePatterns) = ParseCustom(req.Custom);
        var files = new List<(string, long, DateTime)>();
        long bytes = 0;

        void Walk(DirectoryInfo dir, string rel)
        {
            ct.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos(); }
            catch { return; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string childRel = rel.Length == 0 ? e.Name : rel + "\\" + e.Name;
                if (e is DirectoryInfo sub)
                {
                    if (req.SkipGit && sub.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                    if (exclude.Contains(childRel)) continue;
                    if (MatchesAny(dirPatterns, sub.Name)) continue;
                    Walk(sub, childRel);
                }
                else if (e is FileInfo fi)
                {
                    if (MatchesAny(filePatterns, fi.Name)) continue;
                    files.Add((childRel, fi.Length, fi.LastWriteTimeUtc));
                    bytes += fi.Length;
                }
            }
        }

        Walk(new DirectoryInfo(req.Path), "");
        totalBytes = bytes;
        return files;
    }

    private static (List<System.Text.RegularExpressions.Regex> dirs, List<System.Text.RegularExpressions.Regex> files)
        ParseCustom(List<string> patterns)
    {
        var dirs = new List<System.Text.RegularExpressions.Regex>();
        var fils = new List<System.Text.RegularExpressions.Regex>();
        foreach (var raw in patterns)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            bool isDir = p.EndsWith('/') || p.EndsWith('\\');
            p = p.TrimEnd('/', '\\');
            var rx = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (isDir) dirs.Add(rx);
            else if (p.Contains('*') || p.Contains('?')) fils.Add(rx);
            else { dirs.Add(rx); fils.Add(rx); } // bare name: match either
        }
        return (dirs, fils);
    }

    private static bool MatchesAny(List<System.Text.RegularExpressions.Regex> rules, string name)
        => rules.Any(r => r.IsMatch(name));

    // ------------------------------------------------------------------ writers

    private static void WriteMirror(string srcRoot, string stageDir,
        List<(string Rel, long Len, DateTime MtimeUtc)> files, string manifest,
        ProgressFn progress, long totalBytes, CancellationToken ct)
    {
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        Directory.CreateDirectory(stageDir);
        long done = 0; int n = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string src = Path.Combine(srcRoot, f.Rel);
            string dst = Path.Combine(stageDir, f.Rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, true);
            File.SetLastWriteTimeUtc(dst, f.MtimeUtc);
            done += f.Len; n++;
            if ((n & 15) == 0 || done == totalBytes)
                progress("copy", n, files.Count, done, totalBytes, f.Rel);
        }
        File.WriteAllText(Path.Combine(stageDir, "leanback-manifest.json"), manifest);
        progress("copy", files.Count, files.Count, totalBytes, totalBytes, "");
    }

    private static void WriteZip(string srcRoot, string stageFile,
        List<(string Rel, long Len, DateTime MtimeUtc)> files, string manifest,
        ProgressFn progress, long totalBytes, CancellationToken ct)
    {
        if (File.Exists(stageFile)) File.Delete(stageFile);
        long done = 0; int n = 0;
        using var fs = new FileStream(stageFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = za.CreateEntry(f.Rel.Replace('\\', '/'), CompressionLevel.Fastest);
            entry.LastWriteTime = f.MtimeUtc.ToLocalTime();
            using (var src = File.OpenRead(Path.Combine(srcRoot, f.Rel)))
            using (var es = entry.Open())
                src.CopyTo(es);
            done += f.Len; n++;
            if ((n & 15) == 0 || done == totalBytes)
                progress("copy", n, files.Count, done, totalBytes, f.Rel);
        }
        var man = za.CreateEntry("leanback-manifest.json", CompressionLevel.Optimal);
        using (var ms = new StreamWriter(man.Open())) ms.Write(manifest);
        progress("copy", files.Count, files.Count, totalBytes, totalBytes, "");
    }

    // ------------------------------------------------------------------ verify

    // Every file: exists + exact size. Content hash (xxHash64): all files ≤ 8 MB plus a
    // deterministic sample of 32 larger ones.
    public static string? VerifyAgainstSource(BackupRequest req, string backupDir, CancellationToken ct)
    {
        var files = EnumerateFiles(req, ct, out long total);
        return VerifyMirror(req.Path, backupDir, files, static (_, _, _, _, _, _) => { }, total, ct);
    }

    private static string? VerifyMirror(string srcRoot, string stageDir,
        List<(string Rel, long Len, DateTime MtimeUtc)> files,
        ProgressFn progress, long totalBytes, CancellationToken ct)
    {
        const long hashCap = 8L * 1024 * 1024;
        var bigSample = PickSample(files.Where(f => f.Len > hashCap).ToList(), 32);
        long done = 0; int n = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string dst = Path.Combine(stageDir, f.Rel);
            var fi = new FileInfo(dst);
            if (!fi.Exists) return "Missing in backup: " + f.Rel;
            if (fi.Length != f.Len) return "Size mismatch: " + f.Rel;
            if (f.Len <= hashCap || bigSample.Contains(f.Rel))
            {
                if (HashFile(Path.Combine(srcRoot, f.Rel)) != HashFile(dst))
                    return "Content mismatch: " + f.Rel;
            }
            done += f.Len; n++;
            if ((n & 31) == 0) progress("verify", n, files.Count, done, totalBytes, f.Rel);
        }
        progress("verify", files.Count, files.Count, totalBytes, totalBytes, "");
        return null;
    }

    private static string? VerifyZip(string srcRoot, string stageFile,
        List<(string Rel, long Len, DateTime MtimeUtc)> files,
        ProgressFn progress, long totalBytes, CancellationToken ct)
    {
        const long hashCap = 8L * 1024 * 1024;
        var bigSample = PickSample(files.Where(f => f.Len > hashCap).ToList(), 32);
        using var za = ZipFile.OpenRead(stageFile);
        var entries = za.Entries.ToDictionary(e => e.FullName, StringComparer.OrdinalIgnoreCase);
        if (entries.Count != files.Count + 1) // +1 manifest
            return "Entry count mismatch: expected " + (files.Count + 1) + ", found " + entries.Count;
        long done = 0; int n = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            string key = f.Rel.Replace('\\', '/');
            if (!entries.TryGetValue(key, out var e)) return "Missing in zip: " + f.Rel;
            if (e.Length != f.Len) return "Size mismatch: " + f.Rel;
            if (f.Len <= hashCap || bigSample.Contains(f.Rel))
            {
                var h = new XxHash64();
                using var es = e.Open();
                var buf = new byte[1 << 16];
                int r;
                while ((r = es.Read(buf, 0, buf.Length)) > 0) h.Append(buf.AsSpan(0, r));
                if (BitConverter.ToUInt64(h.GetCurrentHash()) != HashFile(Path.Combine(srcRoot, f.Rel)))
                    return "Content mismatch: " + f.Rel;
            }
            done += f.Len; n++;
            if ((n & 31) == 0) progress("verify", n, files.Count, done, totalBytes, f.Rel);
        }
        progress("verify", files.Count, files.Count, totalBytes, totalBytes, "");
        return null;
    }

    private static HashSet<string> PickSample(List<(string Rel, long Len, DateTime MtimeUtc)> big, int count)
    {
        // deterministic: largest N
        return big.OrderByDescending(f => f.Len).Take(count).Select(f => f.Rel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ulong HashFile(string path)
    {
        var h = new XxHash64();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var buf = new byte[1 << 16];
        int r;
        while ((r = fs.Read(buf, 0, buf.Length)) > 0) h.Append(buf.AsSpan(0, r));
        return BitConverter.ToUInt64(h.GetCurrentHash());
    }

    // ------------------------------------------------------------------ retention

    private static void DeleteOldBackups(string destDir, string projName, string keepName)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            "^" + System.Text.RegularExpressions.Regex.Escape(projName) +
            @"_\d{4}-\d{2}-\d{2}_\d{4}(\.zip)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var entry in Directory.EnumerateFileSystemEntries(destDir))
        {
            string name = Path.GetFileName(entry);
            if (name.Equals(keepName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!rx.IsMatch(name)) continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            catch { /* locked old backup: leave it */ }
        }
    }

    private static void CleanupStale(string destDir, string projName)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(destDir))
        {
            string name = Path.GetFileName(entry);
            if (!name.StartsWith(projName + "_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            catch { }
        }
    }

    private static void TryDelete(string stagePath, bool zip)
    {
        try
        {
            if (zip) { if (File.Exists(stagePath)) File.Delete(stagePath); }
            else if (Directory.Exists(stagePath)) Directory.Delete(stagePath, true);
        }
        catch { }
    }

    // ------------------------------------------------------------------ manifest

    private static string BuildManifest(BackupRequest req, string projName, int files, long bytes)
    {
        return JsonSerializer.Serialize(new
        {
            app = "LeanBack",
            version = AppVersion,
            source = req.Path,
            sourceName = projName,
            createdUtc = DateTime.UtcNow.ToString("o"),
            format = req.Format,
            files,
            bytes,
            skipGit = req.SkipGit,
            customPatterns = req.Custom,
            excluded = req.Skipped.Select(s => new { rel = s.Rel, bytes = s.Bytes, reason = s.Reason }),
            regenCommands = req.Regen.Select(r => new { cmd = r.Cmd, brings = r.Brings }),
        }, JsonOpts);
    }

    // ------------------------------------------------------------------ restore

    public static (bool ok, string? error, List<RegenCmd> regen, string target) Restore(
        string backupPath, string targetDir, ProgressFn progress, CancellationToken ct)
    {
        var regen = new List<RegenCmd>();
        try
        {
            bool zip = backupPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileNameWithoutExtension(Path.GetFileName(backupPath));
            // strip _YYYY-MM-DD_HHmm stamp for the restored folder name
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)_\d{4}-\d{2}-\d{2}_\d{4}$");
            string projName = m.Success ? m.Groups[1].Value : name;
            string target = Path.Combine(targetDir, projName);
            Directory.CreateDirectory(target);

            if (zip)
            {
                using var za = ZipFile.OpenRead(backupPath);
                long total = za.Entries.Sum(e => e.Length);
                long done = 0; int n = 0;
                foreach (var e in za.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (e.FullName.EndsWith("/")) continue;
                    string dst = Path.Combine(target, e.FullName.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    e.ExtractToFile(dst, true);
                    done += e.Length; n++;
                    if ((n & 15) == 0) progress("copy", n, za.Entries.Count, done, total, e.FullName);
                }
            }
            else
            {
                var files = new List<(string Rel, long Len)>();
                long total = 0;
                var root = new DirectoryInfo(backupPath);
                void Collect(DirectoryInfo d, string rel)
                {
                    foreach (var e in d.EnumerateFileSystemInfos())
                    {
                        string childRel = rel.Length == 0 ? e.Name : rel + "\\" + e.Name;
                        if (e is FileInfo fi) { files.Add((childRel, fi.Length)); total += fi.Length; }
                        else Collect((DirectoryInfo)e, childRel);
                    }
                }
                Collect(root, "");
                long done = 0; int n = 0;
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string dst = Path.Combine(target, f.Rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(Path.Combine(backupPath, f.Rel), dst, true);
                    done += f.Len; n++;
                    if ((n & 15) == 0) progress("copy", n, files.Count, done, total, f.Rel);
                }
            }

            // read regen commands from the manifest inside the restored copy
            try
            {
                string manPath = Path.Combine(target, "leanback-manifest.json");
                if (File.Exists(manPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manPath));
                    if (doc.RootElement.TryGetProperty("regenCommands", out var arr))
                        foreach (var el in arr.EnumerateArray())
                            regen.Add(new RegenCmd
                            {
                                Cmd = el.GetProperty("cmd").GetString() ?? "",
                                Brings = el.GetProperty("brings").GetString() ?? "",
                            });
                }
            }
            catch { }

            return (true, null, regen, target);
        }
        catch (OperationCanceledException) { return (false, "cancelled", regen, ""); }
        catch (Exception ex) { return (false, ex.Message, regen, ""); }
    }
}
