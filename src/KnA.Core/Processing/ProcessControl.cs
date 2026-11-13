using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KnA.Core.Processing;

/// <summary>Cross-platform pause / resume / kill for child processes.</summary>
public static partial class ProcessControl
{
    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr processHandle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(IntPtr processHandle);

    public static void Suspend(Process p)
    {
        if (p.HasExited) return;
        if (OperatingSystem.IsWindows()) NtSuspendProcess(p.Handle);
        else Signal(p.Id, "STOP");
    }

    public static void Resume(Process p)
    {
        if (p.HasExited) return;
        if (OperatingSystem.IsWindows()) NtResumeProcess(p.Handle);
        else Signal(p.Id, "CONT");
    }

    public static void KillTree(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    // Signal numbers differ between Linux and macOS, so let `kill` resolve the names.
    private static void Signal(int pid, string signal)
    {
        using var k = Process.Start(new ProcessStartInfo("kill", $"-{signal} {pid}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        k?.WaitForExit(2000);
    }
}
