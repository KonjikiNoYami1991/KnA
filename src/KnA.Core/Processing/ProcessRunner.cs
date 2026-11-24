using System.Diagnostics;
using System.Text;

namespace KnA.Core.Processing;

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
        // Programs unpacked from a .deb by KnA (NVEncC, QSVEncC, VCEEncC…) keep their private libraries in usr/lib.
        if (OperatingSystem.IsLinux() && PrivateLibraryPath(fileName) is { } libs)
            psi.Environment["LD_LIBRARY_PATH"] = libs + (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") is { Length: > 0 } old ? ":" + old : "");
        // ArgumentList quotes every argument correctly: no more broken paths with spaces/quotes.
        var list = args as IReadOnlyCollection<string> ?? args.ToList();
        foreach (var a in list) psi.ArgumentList.Add(a);
        // KnA's portable VapourSynth (Linux/macOS): the programs that load it read its generated configuration.
        Tools.VapourSynthRuntime.Apply(psi, fileName, list);
        return psi;
    }

    /// <summary>Library folders under "usr/lib" next to a program unpacked from a package, or null.</summary>
    public static string? PrivateLibraryPath(string fileName)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(fileName));
            if (dir is null) return null;
            var root = Path.Combine(dir, "usr", "lib");
            if (!Directory.Exists(root)) return null;
            var dirs = Directory.EnumerateFiles(root, "*.so*", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).OfType<string>().Distinct().ToList();
            return dirs.Count > 0 ? string.Join(':', dirs) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
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
