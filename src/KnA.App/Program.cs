using Avalonia;
using KnA.App.Platform;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Send To with many files / a second double click: hand the files to the window already open.
        if (SingleInstance.TryForward(args)) return 0;

        AppUpdater.CleanupOldFiles();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) TryCrashLog(ex, "unhandled");
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { TryCrashLog(e.Exception, "task"); e.SetObserved(); };
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            TryCrashLog(ex, "main");
            throw;
        }
    }

    private static void TryCrashLog(Exception ex, string where)
    {
        try { new SettingsStore().WriteCrashLog(ex, where); }
        catch (Exception) { /* nothing left to do */ }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
