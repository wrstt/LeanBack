using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LeanBack;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Hidden headless mode for scripting/testing:
        //   LeanBack.exe --cli scan    <projectPath> <out.json>
        //   LeanBack.exe --cli backup  <request.json> <out.json>
        //   LeanBack.exe --cli verify  <request.json> <backupDir> <out.json>
        //   LeanBack.exe --cli restore <backupPath> <targetDir> <out.json>
        if (args.Length >= 1 && args[0] == "--cli")
            return Cli.Run(args.Skip(1).ToArray());

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }
}
