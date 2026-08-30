using CommunityToolkit.Mvvm.ComponentModel;
using LeanBack.Engine;

namespace LeanBack.ViewModels;

public partial class ProjectItem : ObservableObject
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "Folder";
    public long TotalBytes { get; init; }
    public long KeptBytes { get; init; }

    public string SizeText => BackupEngine.Fmt(TotalBytes);
    public string KeptText => BackupEngine.Fmt(KeptBytes);

    public RecentProject ToRecent() => new()
    {
        Path = Path, Name = Name, Kind = Kind, TotalBytes = TotalBytes, KeptBytes = KeptBytes,
    };

    public static ProjectItem From(RecentProject r) => new()
    {
        Path = r.Path, Name = r.Name, Kind = r.Kind, TotalBytes = r.TotalBytes, KeptBytes = r.KeptBytes,
    };

    public static ProjectItem From(ScanResult s) => new()
    {
        Path = s.Path, Name = s.Name, Kind = s.Kind, TotalBytes = s.TotalBytes, KeptBytes = s.KeptBytes,
    };
}

/// <summary>One reinstallable directory the user can choose to skip.</summary>
public partial class SkipRow : ObservableObject
{
    [ObservableProperty] private bool _isSkipped;

    public string Rel { get; init; } = "";
    public string Reason { get; init; } = "";
    public long Bytes { get; init; }
    public int Files { get; init; }

    public string SizeText => BackupEngine.Fmt(Bytes);
    public string FilesText => $"{Files:N0} files";
}

public partial class HistoryItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string BackupPath { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public long Bytes { get; init; }
    public int Files { get; init; }
    public string CreatedUtc { get; init; } = "";
    public string Format { get; init; } = "mirror";
    public List<RegenCmd> Regen { get; init; } = new();

    public string SizeText => BackupEngine.Fmt(Bytes);

    public string WhenText =>
        DateTime.TryParse(CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("d MMM yyyy, HH:mm")
            : CreatedUtc;

    public HistoryEntry ToEntry() => new()
    {
        Name = Name, BackupPath = BackupPath, ProjectPath = ProjectPath, ProjectName = ProjectName,
        Bytes = Bytes, Files = Files, CreatedUtc = CreatedUtc, Format = Format, Regen = Regen,
    };

    public static HistoryItem From(HistoryEntry e) => new()
    {
        Name = e.Name, BackupPath = e.BackupPath, ProjectPath = e.ProjectPath, ProjectName = e.ProjectName,
        Bytes = e.Bytes, Files = e.Files, CreatedUtc = e.CreatedUtc, Format = e.Format, Regen = e.Regen,
    };
}

/// <summary>A step on the running screen: Pending -> Active -> Done.</summary>
public partial class RunStep : ObservableObject
{
    [ObservableProperty] private string _label = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pending))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pending))]
    private bool _isDone;

    /// <summary>Not started yet — drawn as a hollow dot.</summary>
    public bool Pending => !IsActive && !IsDone;

    public RunStep(string label) => _label = label;
}
