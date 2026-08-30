using System.Text.Json;
using LeanBack.Engine;

namespace LeanBack;

/// <summary>Headless entry points used by the acceptance tests. UI-free by design.</summary>
internal static class Cli
{
    public static int Run(string[] a)
    {
        var camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        try
        {
            switch (a[0])
            {
                case "scan":
                {
                    var result = Scanner.Scan(a[1], null, CancellationToken.None);
                    File.WriteAllText(a[2], JsonSerializer.Serialize(result, camel));
                    return 0;
                }
                case "backup":
                {
                    var req = JsonSerializer.Deserialize<BackupRequest>(File.ReadAllText(a[1]), camel)!;
                    var outcome = BackupEngine.Run(req, static (_, _, _, _, _, _) => { }, CancellationToken.None);
                    File.WriteAllText(a[2], JsonSerializer.Serialize(outcome, camel));
                    return outcome.Ok ? 0 : 1;
                }
                case "verify":
                {
                    var req = JsonSerializer.Deserialize<BackupRequest>(File.ReadAllText(a[1]), camel)!;
                    string? err = BackupEngine.VerifyAgainstSource(req, a[2], CancellationToken.None);
                    File.WriteAllText(a[3], JsonSerializer.Serialize(new { ok = err == null, error = err }, camel));
                    return err == null ? 0 : 1;
                }
                case "restore":
                {
                    var (ok, error, regen, target) = BackupEngine.Restore(a[1], a[2],
                        static (_, _, _, _, _, _) => { }, CancellationToken.None);
                    File.WriteAllText(a[3], JsonSerializer.Serialize(new { ok, error, regen, target }, camel));
                    return ok ? 0 : 1;
                }
                default:
                    return 2;
            }
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(a[^1], JsonSerializer.Serialize(new { ok = false, error = ex.ToString() }, camel)); }
            catch { }
            return 1;
        }
    }
}
