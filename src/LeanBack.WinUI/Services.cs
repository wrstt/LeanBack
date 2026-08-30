using System.Diagnostics;
using System.Text.Json;
using LeanBack.Engine;
using Windows.Storage.Pickers;

namespace LeanBack;

/// <summary>Reads/writes leanback.json through the engine's portable SettingsStore.</summary>
internal static class SettingsService
{
    private static readonly JsonSerializerOptions Camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static CancellationTokenSource? _debounce;

    public static AppSettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(SettingsStore.Load(), Camel);
            return s ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>Coalesces bursts of edits (checkbox toggles) into one write, as the old UI did.</summary>
    public static void SaveDebounced(AppSettings s)
    {
        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                SettingsStore.Save(JsonSerializer.Serialize(s, Camel));
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }
}

internal static class Shell
{
    public static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public static async Task<string?> PickFolderAsync(IntPtr hwnd)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        // Unpackaged apps must associate the picker with the owning window themselves.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
