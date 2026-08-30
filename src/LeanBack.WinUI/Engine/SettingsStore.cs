namespace LeanBack.Engine;

/// <summary>
/// Persists UI state as an opaque JSON blob: leanback.json beside the exe when that
/// location is writable (true portable mode), else %APPDATA%\LeanBack\leanback.json.
/// </summary>
public static class SettingsStore
{
    private static string? _path;

    public static string FilePath => _path ??= Resolve();

    private static string Resolve()
    {
        string exeDir = AppContext.BaseDirectory;
        string portable = Path.Combine(exeDir, "leanback.json");
        try
        {
            // probe writability without clobbering an existing file
            string probe = Path.Combine(exeDir, ".lb-writetest-" + Environment.ProcessId);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return portable;
        }
        catch
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanBack");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "leanback.json");
        }
    }

    public static string Load()
    {
        try
        {
            // portable file wins; fall back to appdata copy if beside-exe was never writable
            if (File.Exists(FilePath)) return File.ReadAllText(FilePath);
        }
        catch { }
        return "null";
    }

    public static void Save(string json)
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch { /* read-only media: state just isn't persisted */ }
    }
}
