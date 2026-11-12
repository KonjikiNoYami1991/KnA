using System.Diagnostics;
using TnA.Core.Encoding;

namespace TnA.Core.Tools;

/// <summary>What to do when the queue is finished.</summary>
public static class PowerActions
{
    public static void Run(PostQueueAction action, Action closeApp)
    {
        switch (action)
        {
            case PostQueueAction.CloseApp:
                closeApp();
                break;
            case PostQueueAction.Sleep:
                if (OperatingSystem.IsWindows()) Start("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
                else if (OperatingSystem.IsMacOS()) Start("pmset", "sleepnow");
                else Start("systemctl", "suspend");
                break;
            case PostQueueAction.Shutdown:
                // Windows: 2 minutes of grace, can be aborted with "shutdown /a".
                if (OperatingSystem.IsWindows()) Start("shutdown", "/s /t 120");
                else if (OperatingSystem.IsMacOS()) Start("osascript", "-e \"tell app \\\"System Events\\\" to shut down\"");
                else Start("systemctl", "poweroff");
                break;
        }
    }

    private static void Start(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true }); }
        catch (Exception) { /* best effort */ }
    }
}
