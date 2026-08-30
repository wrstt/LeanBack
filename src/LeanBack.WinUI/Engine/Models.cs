namespace LeanBack.Engine;

public sealed class FlaggedDir
{
    public string Rel { get; set; } = "";          // relative path from project root, backslash separated
    public long Bytes { get; set; }
    public int Files { get; set; }
    public string Reason { get; set; } = "";       // chip text, e.g. "npm install", "build output", "gitignored"
    public bool DefaultChecked { get; set; } = true;
}

public sealed class RegenCmd
{
    public string Cmd { get; set; } = "";
    public string Brings { get; set; } = "";
}

public sealed class TopEntry
{
    public string Name { get; set; } = "";
    public bool IsDir { get; set; }
}

public sealed class ScanResult
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Folder";
    public long TotalBytes { get; set; }           // everything on disk incl. flagged dirs and .git
    public long KeptBytes { get; set; }            // bytes outside flagged dirs and .git
    public int KeptFiles { get; set; }
    public int TotalFiles { get; set; }
    public long GitBytes { get; set; }
    public bool HasGit { get; set; }
    public List<FlaggedDir> Rows { get; set; } = new();
    public List<RegenCmd> RegenCmds { get; set; } = new();
    public List<TopEntry> TopLevel { get; set; } = new();
}

public sealed class BackupRequest
{
    public string Path { get; set; } = "";
    public string Dest { get; set; } = "";         // drive root like "E:\"
    public string Format { get; set; } = "mirror"; // mirror | zip
    public List<string> Exclude { get; set; } = new();  // rel paths of dirs to skip
    public bool SkipGit { get; set; } = true;
    public List<string> Custom { get; set; } = new();   // custom skip patterns
    public List<RegenCmd> Regen { get; set; } = new();
    public List<FlaggedDir> Skipped { get; set; } = new(); // for the manifest: what was skipped and why
}

public sealed class BackupOutcome
{
    public bool Ok { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public string BackupPath { get; set; } = "";
    public string Name { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
}

public sealed class DriveEntry
{
    public string Root { get; set; } = "";         // "E:\"
    public string Display { get; set; } = "";      // "E:\ — USB Flash (28.4 GB free)"
    public string Type { get; set; } = "";
    public long FreeBytes { get; set; }
}

public delegate void ProgressFn(string phase, int filesDone, int filesTotal, long bytesDone, long bytesTotal, string current);
