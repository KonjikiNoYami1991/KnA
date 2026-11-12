using System.Diagnostics;
using System.Text;

namespace TnA.Core.Processing;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Small helper to run a short-lived command and capture its output.</summary>
public static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;
        // ArgumentList quotes every argument correctly: no more broken paths with spaces/quotes.
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args,
        string? workingDirectory = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var p = new Process { StartInfo = CreateStartInfo(fileName, args, workingDirectory) };
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            ProcessControl.KillTree(p);
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, "", "Timed out.");
        }
        return new ProcessResult(p.ExitCode, await stdout, await stderr);
    }
}
