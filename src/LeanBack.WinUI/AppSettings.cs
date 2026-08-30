using System.Text.Json.Serialization;
using LeanBack.Engine;

namespace LeanBack;

/// <summary>
/// On-disk shape of leanback.json. Field-for-field compatible with the WebView2 build so an
/// existing settings file (recents, history, per-project skip choices) survives the upgrade.
/// </summary>
public sealed class AppSettings
{
    public string Mode { get; set; } = "simple";              // simple | advanced
    public string? Dest { get; set; }                          // drive root like "E:\"
    public string? Format { get; set; }                        // mirror | zip | null
    public List<RecentProject> Projects { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();

    /// <summary>projectPath -> (relative dir -> checked). Overrides FlaggedDir.DefaultChecked.</summary>
    public Dictionary<string, Dictionary<string, bool>> Checks { get; set; } = new();

    /// <summary>projectPath -> skip .git (defaults to true when absent).</summary>
    public Dictionary<string, bool> SkipGitMap { get; set; } = new();

    /// <summary>projectPath -> custom skip patterns.</summary>
    public Dictionary<string, List<string>> Custom { get; set; } = new();
}

public sealed class RecentProject
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Folder";
    public long TotalBytes { get; set; }
    public long KeptBytes { get; set; }
}

public sealed class HistoryEntry
{
    public string ProjectPath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string Name { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public long Bytes { get; set; }
    public int Files { get; set; }
    public string CreatedUtc { get; set; } = "";
    public string Format { get; set; } = "mirror";
    public List<RegenCmd> Regen { get; set; } = new();
}
