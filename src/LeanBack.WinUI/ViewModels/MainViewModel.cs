using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanBack.Engine;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeanBack.ViewModels;

public enum AppScreen { Setup, Running, Done }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dq = DispatcherQueue.GetForCurrentThread();
    private AppSettings _settings = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _runCts;
    private bool _suppressScan;

    /// <summary>
    /// Guards against writing settings before they've been read. The SelectorBar raises a
    /// selection change during XAML load, which would otherwise persist mode/format defaults
    /// over the real file.
    /// </summary>
    private bool _ready;

    /// <summary>Owning window handle; the folder picker needs it when unpackaged.</summary>
    public IntPtr Hwnd { get; set; }

    // ---------------------------------------------------------------- screen

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    private AppScreen _screen = AppScreen.Setup;

    public bool IsSetup => Screen == AppScreen.Setup;
    public bool IsRunning => Screen == AppScreen.Running;
    public bool IsDone => Screen == AppScreen.Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSimple))]
    [NotifyPropertyChangedFor(nameof(IsAdvanced))]
    private bool _advanced;

    public bool IsSimple => !Advanced;
    public bool IsAdvanced => Advanced;

    // ---------------------------------------------------------------- data

    public ObservableCollection<DriveEntry> Drives { get; } = new();
    public ObservableCollection<ProjectItem> Projects { get; } = new();
    public ObservableCollection<SkipRow> SkipRows { get; } = new();
    public ObservableCollection<string> CustomPatterns { get; } = new();
    public ObservableCollection<HistoryItem> History { get; } = new();
    public ObservableCollection<RunStep> Steps { get; } = new();
    public ObservableCollection<RegenCmd> RegenCmds { get; } = new();

    [ObservableProperty] private DriveEntry? _destDrive;
    [ObservableProperty] private ProjectItem? _selectedProject;
    [ObservableProperty] private string _customInput = "";
    [ObservableProperty] private bool _dryRun;
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _scanStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMirror))]
    [NotifyPropertyChangedFor(nameof(IsZip))]
    private string? _format;

    public bool IsMirror => Format == "mirror";
    public bool IsZip => Format == "zip";

    /// <summary>
    /// Index for the RadioButtons container: 0 mirror, 1 zip, -1 nothing chosen yet.
    /// Individual RadioButtons with a shared GroupName don't hold a one-way IsChecked binding,
    /// so the selection is driven by index instead.
    /// </summary>
    public int FormatIndex
    {
        get => Format switch { "mirror" => 0, "zip" => 1, _ => -1 };
        set
        {
            string? next = value switch { 0 => "mirror", 1 => "zip", _ => null };
            if (next is not null && next != Format) Format = next;
        }
    }

    [ObservableProperty] private bool _skipGit = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScan))]
    [NotifyPropertyChangedFor(nameof(HasGit))]
    private ScanResult? _scan;

    public bool HasScan => Scan is not null;
    public bool HasGit => Scan?.HasGit == true;

    // ---------------------------------------------------------------- derived totals

    /// <summary>
    /// Mirrors the old calc(): skipped is the sum of checked rows plus .git when skipped,
    /// and kept is whatever is left of the total — not ScanResult.KeptBytes.
    /// </summary>
    public long SkippedBytes
    {
        get
        {
            if (Scan is null) return 0;
            long skipped = 0;
            foreach (var r in SkipRows)
                if (r.IsSkipped) skipped += r.Bytes;
            if (SkipGit) skipped += Scan.GitBytes;
            return skipped;
        }
    }

    public long KeptBytes => Scan is null ? 0 : Math.Max(0, Scan.TotalBytes - SkippedBytes);

    public int SkippedPct => Scan is null || Scan.TotalBytes <= 0
        ? 0
        : (int)Math.Round(100.0 * SkippedBytes / Scan.TotalBytes);

    public string SummaryText => Scan is null
        ? ""
        : $"{BackupEngine.Fmt(KeptBytes)} to copy · {BackupEngine.Fmt(SkippedBytes)} skipped ({SkippedPct}%)";

    public string KeptText => BackupEngine.Fmt(KeptBytes);
    public string SkippedText => BackupEngine.Fmt(SkippedBytes);
    public string TotalText => Scan is null ? "" : BackupEngine.Fmt(Scan.TotalBytes);
    public string PctText => $"{SkippedPct}% LEFT BEHIND";

    // Star widths drive the ratio bar, so the split re-lays out as rows are toggled.
    // Floored at 1 so a zero side stays a visible sliver rather than vanishing.
    public GridLength KeptStar => new(Math.Max(1L, KeptBytes), GridUnitType.Star);
    public GridLength SkippedStar => new(Math.Max(1L, SkippedBytes), GridUnitType.Star);

    public bool CanBackup => Scan is not null && !Scanning && DestDrive is not null;

    public string CtaLabel => Scan is null
        ? "Back up"
        : $"Back up {BackupEngine.Fmt(KeptBytes)} → {DestDrive?.Root ?? "?"}";

    // ---------------------------------------------------------------- running / done

    [ObservableProperty] private string _runTitle = "";
    [ObservableProperty] private string _runSubtitle = "";
    [ObservableProperty] private double _runPercent;
    [ObservableProperty] private string _runDetail = "";

    [ObservableProperty] private string _doneName = "";
    [ObservableProperty] private string _doneBackupPath = "";
    [ObservableProperty] private string _doneStats = "";

    // ---------------------------------------------------------------- toast

    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _toastOpen;
    [ObservableProperty] private InfoBarSeverity _toastSeverity = InfoBarSeverity.Informational;

    private void Toast(string text, InfoBarSeverity sev = InfoBarSeverity.Informational)
    {
        ToastText = text;
        ToastSeverity = sev;
        ToastOpen = true;
    }

    // ---------------------------------------------------------------- startup

    public async Task InitialiseAsync()
    {
        _settings = SettingsService.Load();
        Advanced = _settings.Mode == "advanced";
        Format = _settings.Format is "mirror" or "zip" ? _settings.Format : null;

        RefreshDrives();

        _suppressScan = true;
        foreach (var p in _settings.Projects)
            Projects.Add(ProjectItem.From(p));
        SelectedProject = Projects.FirstOrDefault();
        _suppressScan = false;

        _ready = true;

        if (SelectedProject is not null)
            await ScanSelectedAsync();
    }

    public void RefreshDrives()
    {
        string? want = DestDrive?.Root ?? _settings.Dest;
        Drives.Clear();
        foreach (var d in BackupEngine.ListDrives()) Drives.Add(d);

        DestDrive = Drives.FirstOrDefault(d => d.Root == want)
                    ?? Drives.FirstOrDefault(d => !d.Root.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                    ?? Drives.FirstOrDefault();
    }

    // ---------------------------------------------------------------- change hooks

    partial void OnSelectedProjectChanged(ProjectItem? value)
    {
        if (_suppressScan || value is null) return;
        _ = ScanSelectedAsync();
    }

    partial void OnDestDriveChanged(DriveEntry? value)
    {
        OnPropertyChanged(nameof(CanBackup));
        OnPropertyChanged(nameof(CtaLabel));
        Persist();
    }

    partial void OnScanningChanged(bool value) => OnPropertyChanged(nameof(CanBackup));

    partial void OnAdvancedChanged(bool value) => Persist();

    partial void OnFormatChanged(string? value)
    {
        OnPropertyChanged(nameof(FormatIndex));
        Persist();
    }

    partial void OnScanChanged(ScanResult? value) => RaiseTotals();

    partial void OnSkipGitChanged(bool value)
    {
        if (SelectedProject is not null)
            _settings.SkipGitMap[SelectedProject.Path] = value;
        RaiseTotals();
        Persist();
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(SkippedBytes));
        OnPropertyChanged(nameof(KeptBytes));
        OnPropertyChanged(nameof(SkippedPct));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(CanBackup));
        OnPropertyChanged(nameof(CtaLabel));
        OnPropertyChanged(nameof(KeptText));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(PctText));
        OnPropertyChanged(nameof(KeptStar));
        OnPropertyChanged(nameof(SkippedStar));
    }

    // ---------------------------------------------------------------- scanning

    public async Task ScanSelectedAsync()
    {
        if (SelectedProject is null) return;
        string path = SelectedProject.Path;

        // A recent can outlive its folder; say so plainly instead of surfacing a raw IO error.
        if (!Directory.Exists(path))
        {
            DropMissingProject(SelectedProject);
            return;
        }

        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;

        Scanning = true;
        ScanStatus = "Scanning…";
        Scan = null;
        SkipRows.Clear();

        try
        {
            var result = await Task.Run(() => Scanner.Scan(path, (files, bytes) =>
            {
                _dq.TryEnqueue(() => ScanStatus = $"Scanning… {files:N0} files, {BackupEngine.Fmt(bytes)}");
            }, cts.Token), cts.Token);

            if (!cts.IsCancellationRequested)
                ApplyScan(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Toast($"Couldn't scan {path}: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
            {
                Scanning = false;
                ScanStatus = "";
            }
        }
    }

    /// <summary>A recent whose folder has since moved or been deleted drops off the list.</summary>
    private void DropMissingProject(ProjectItem missing)
    {
        _suppressScan = true;
        Projects.Remove(missing);
        SelectedProject = Projects.FirstOrDefault();
        _suppressScan = false;

        _settings.Projects = Projects.Select(p => p.ToRecent()).ToList();
        Scan = null;
        SkipRows.Clear();
        Scanning = false;
        ScanStatus = "";
        Persist();

        Toast($"{missing.Name} is no longer on disk — removed from recents");

        if (SelectedProject is not null) _ = ScanSelectedAsync();
    }

    private void ApplyScan(ScanResult result)
    {
        _settings.Checks.TryGetValue(result.Path, out var overrides);

        SkipRows.Clear();
        foreach (var r in result.Rows)
        {
            bool skipped = overrides is not null && overrides.TryGetValue(r.Rel, out var v) ? v : r.DefaultChecked;
            var row = new SkipRow
            {
                Rel = r.Rel, Reason = r.Reason, Bytes = r.Bytes, Files = r.Files, IsSkipped = skipped,
            };
            row.PropertyChanged += OnSkipRowChanged;
            SkipRows.Add(row);
        }

        SkipGit = !_settings.SkipGitMap.TryGetValue(result.Path, out var g) || g;

        CustomPatterns.Clear();
        if (_settings.Custom.TryGetValue(result.Path, out var pats))
            foreach (var p in pats) CustomPatterns.Add(p);

        // Keep-1 retention deletes superseded backups, and users delete them by hand too;
        // drop history rows whose target is gone so Restore can't point at nothing.
        int before = _settings.History.Count;
        _settings.History.RemoveAll(h => !BackupExists(h.BackupPath));
        bool pruned = _settings.History.Count != before;

        History.Clear();
        foreach (var h in _settings.History.Where(h => h.ProjectPath == result.Path))
            History.Add(HistoryItem.From(h));

        Scan = result;
        RememberProject(result);
        RaiseTotals();

        if (pruned) Persist();
    }

    /// <summary>A mirror backup is a directory; a zip backup is a file.</summary>
    private static bool BackupExists(string path)
        => !string.IsNullOrEmpty(path) && (Directory.Exists(path) || File.Exists(path));

    private void OnSkipRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SkipRow.IsSkipped) || sender is not SkipRow row) return;
        if (SelectedProject is null) return;

        if (!_settings.Checks.TryGetValue(SelectedProject.Path, out var map))
            _settings.Checks[SelectedProject.Path] = map = new Dictionary<string, bool>();
        map[row.Rel] = row.IsSkipped;

        RaiseTotals();
        Persist();
    }

    /// <summary>Keeps the recents list to the six most recent, most-recent first.</summary>
    private void RememberProject(ScanResult s)
    {
        var item = ProjectItem.From(s);

        _suppressScan = true;
        var existing = Projects.FirstOrDefault(p => p.Path == s.Path);
        if (existing is not null) Projects.Remove(existing);
        Projects.Insert(0, item);
        while (Projects.Count > 6) Projects.RemoveAt(Projects.Count - 1);
        SelectedProject = item;
        _suppressScan = false;

        _settings.Projects = Projects.Select(p => p.ToRecent()).ToList();
        Persist();
    }

    private void Persist()
    {
        if (!_ready) return;
        _settings.Mode = Advanced ? "advanced" : "simple";
        _settings.Dest = DestDrive?.Root;
        _settings.Format = Format;
        SettingsService.SaveDebounced(_settings);
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private async Task BrowseAsync()
    {
        string? path = await Shell.PickFolderAsync(Hwnd);
        if (string.IsNullOrEmpty(path)) return;

        _scanCts?.Cancel();
        Scanning = true;
        try
        {
            var result = await Task.Run(() => Scanner.Scan(path, null, CancellationToken.None));
            ApplyScan(result);
        }
        catch (Exception ex)
        {
            Toast($"Couldn't scan that folder: {ex.Message}", InfoBarSeverity.Error);
        }
        finally { Scanning = false; }
    }

    [RelayCommand]
    private void AddCustom()
    {
        string v = CustomInput.Trim();
        if (v.Length == 0 || SelectedProject is null) return;
        if (CustomPatterns.Contains(v)) { CustomInput = ""; return; }

        CustomPatterns.Add(v);
        _settings.Custom[SelectedProject.Path] = CustomPatterns.ToList();
        CustomInput = "";
        Persist();
        Toast($"Will skip \"{v}\" in {SelectedProject.Name}");
    }

    [RelayCommand]
    private void RemoveCustom(string pattern)
    {
        if (SelectedProject is null) return;
        CustomPatterns.Remove(pattern);
        _settings.Custom[SelectedProject.Path] = CustomPatterns.ToList();
        Persist();
    }

    [RelayCommand]
    private void SetMirror() => Format = "mirror";

    [RelayCommand]
    private void SetZip() => Format = "zip";

    [RelayCommand]
    private void OpenBackup()
    {
        if (!string.IsNullOrEmpty(DoneBackupPath)) Shell.Reveal(DoneBackupPath);
    }

    [RelayCommand]
    private void RevealHistory(HistoryItem h) => Shell.Reveal(h.BackupPath);

    [RelayCommand]
    private void DoneBack()
    {
        Screen = AppScreen.Setup;
        _ = ScanSelectedAsync();
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    // ---------------------------------------------------------------- backup

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (Scan is null || SelectedProject is null) return;
        if (DestDrive is null) { Toast("No destination drives found — plug one in"); return; }

        string format = Format ?? (IsSimple ? "mirror" : "");
        if (format.Length == 0) { Toast("Pick a format first: folder copy or .zip"); return; }

        var scan = Scan;
        var skipped = SkipRows.Where(r => r.IsSkipped).ToList();
        long keptSnapshot = KeptBytes;
        int pctSnapshot = SkippedPct;

        var req = new BackupRequest
        {
            Path = SelectedProject.Path,
            Dest = DestDrive.Root,
            Format = format,
            Exclude = skipped.Select(r => r.Rel).ToList(),
            SkipGit = SkipGit,
            Custom = CustomPatterns.ToList(),
            Regen = scan.RegenCmds,
            Skipped = skipped.Select(r => new FlaggedDir
            {
                Rel = r.Rel, Bytes = r.Bytes, Files = r.Files, Reason = r.Reason, DefaultChecked = true,
            }).ToList(),
        };

        RunTitle = $"Backing up {SelectedProject.Name}";
        RunSubtitle = "Copying only what can't be reinstalled";
        Steps.Clear();
        Steps.Add(new RunStep("Scanning tree"));
        Steps.Add(new RunStep("Skipping reinstallable dirs"));
        Steps.Add(new RunStep($"Copying {BackupEngine.Fmt(keptSnapshot)}"));
        Steps.Add(new RunStep("Verifying checksums"));
        SetStep(0);
        RunPercent = 0;
        RunDetail = "";
        Screen = AppScreen.Running;

        var cts = new CancellationTokenSource();
        _runCts = cts;

        try
        {
            var outcome = await Task.Run(() => BackupEngine.Run(req, OnBackupProgress, cts.Token), cts.Token);

            if (outcome.Cancelled)
            {
                Screen = AppScreen.Setup;
                Toast("Backup cancelled — nothing was left behind");
                return;
            }
            if (!outcome.Ok)
            {
                Screen = AppScreen.Setup;
                Toast(outcome.Error ?? "Backup failed", InfoBarSeverity.Error);
                return;
            }

            RecordHistory(outcome, scan, format);

            DoneName = outcome.Name;
            DoneBackupPath = outcome.BackupPath;
            DoneStats = $"{outcome.Files:N0} files · {BackupEngine.Fmt(outcome.Bytes)} · {pctSnapshot}% skipped";
            RegenCmds.Clear();
            foreach (var c in scan.RegenCmds) RegenCmds.Add(c);
            Screen = AppScreen.Done;
        }
        catch (OperationCanceledException)
        {
            Screen = AppScreen.Setup;
            Toast("Backup cancelled — nothing was left behind");
        }
        catch (Exception ex)
        {
            Screen = AppScreen.Setup;
            Toast(ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>Called on a worker thread by the engine; marshals to the UI thread.</summary>
    private void OnBackupProgress(string phase, int filesDone, int filesTotal, long bytesDone, long bytesTotal, string current)
    {
        _dq.TryEnqueue(() =>
        {
            double p = bytesTotal > 0 ? (double)bytesDone / bytesTotal : 0;

            // Same weighting the HTML build used, so the bar moves at a familiar pace.
            switch (phase)
            {
                case "scan": SetStep(0); RunPercent = 3; break;
                case "copy": SetStep(2); RunPercent = (0.05 + p * 0.75) * 100; break;
                case "verify": SetStep(3); RunPercent = (0.80 + p * 0.20) * 100; break;
            }

            RunDetail = filesTotal > 0
                ? $"{filesDone:N0} / {filesTotal:N0} files · {BackupEngine.Fmt(bytesDone)} of {BackupEngine.Fmt(bytesTotal)}"
                : current;
        });
    }

    private void SetStep(int index)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsDone = i < index;
            Steps[i].IsActive = i == index;
        }
    }

    private void RecordHistory(BackupOutcome outcome, ScanResult scan, string format)
    {
        var entry = new HistoryEntry
        {
            ProjectPath = scan.Path,
            ProjectName = scan.Name,
            Name = outcome.Name,
            BackupPath = outcome.BackupPath,
            Bytes = outcome.Bytes,
            Files = outcome.Files,
            CreatedUtc = DateTime.UtcNow.ToString("o"),
            Format = format,
            Regen = scan.RegenCmds,
        };
        _settings.History.Insert(0, entry);
        History.Insert(0, HistoryItem.From(entry));
        Persist();
    }

    // ---------------------------------------------------------------- restore

    [RelayCommand]
    private async Task RestoreAsync(HistoryItem h)
    {
        string? target = await Shell.PickFolderAsync(Hwnd);
        if (string.IsNullOrEmpty(target)) return;

        RunTitle = "Restoring backup";
        RunSubtitle = "Copying your files back";
        Steps.Clear();
        Steps.Add(new RunStep("Reading backup"));
        Steps.Add(new RunStep("Copying files"));
        Steps.Add(new RunStep("Done"));
        SetStep(1);
        RunPercent = 0;
        Screen = AppScreen.Running;

        var cts = new CancellationTokenSource();
        _runCts = cts;

        try
        {
            var (ok, error, regen, dest) = await Task.Run(() => BackupEngine.Restore(
                h.BackupPath, target,
                (phase, fd, ft, bd, bt, cur) => _dq.TryEnqueue(() =>
                {
                    RunPercent = bt > 0 ? 100.0 * bd / bt : 0;
                    RunDetail = ft > 0 ? $"{fd:N0} / {ft:N0} files" : cur;
                }),
                cts.Token), cts.Token);

            Screen = AppScreen.Setup;
            if (!ok)
            {
                Toast(error ?? "Restore failed", InfoBarSeverity.Error);
            }
            else
            {
                Toast($"Restored to {dest}", InfoBarSeverity.Success);
                Shell.Reveal(dest);
            }
        }
        catch (OperationCanceledException)
        {
            Screen = AppScreen.Setup;
            Toast("Restore cancelled");
        }
        catch (Exception ex)
        {
            Screen = AppScreen.Setup;
            Toast(ex.Message, InfoBarSeverity.Error);
        }
    }
}
