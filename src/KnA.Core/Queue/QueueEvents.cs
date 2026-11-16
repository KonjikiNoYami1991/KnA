using System.Diagnostics;
using System.Globalization;
using KnA.Core.Processing;

namespace KnA.Core.Queue;

/// <summary>
/// Commands run after each job and after the whole queue (StaxRip's "events", without its ceremony): one line,
/// with macros, run by the system shell. A failing command is logged, it never fails the job or stops the queue.
/// Macros: {source} {name} {folder} {output} {outputfolder} {status} {message} {duration} {size} {date} {time}
/// (paths are quoted when they are substituted inside the command: write {output}, not "{output}").
/// </summary>
public static class QueueEvents
{
    public static IReadOnlyList<(string Macro, string Description)> Macros { get; } =
    [
        ("{source}", "source file"), ("{name}", "source name without extension"), ("{folder}", "source folder"),
        ("{output}", "output file"), ("{outputfolder}", "output folder"), ("{status}", "Done / Warning / Error / Stopped"),
        ("{message}", "error or warning text"), ("{duration}", "encoding time (s)"), ("{size}", "output size (bytes)"),
        ("{date}", "yyyy-MM-dd"), ("{time}", "HH-mm-ss"),
    ];

    /// <summary>Replaces the macros; paths and free text are quoted for the shell of this system.</summary>
    public static string Expand(string template, QueueJob? job, int done = 0, int failed = 0)
    {
        string Q(string? v) => Quote(v ?? "");
        var output = job?.OutputPath;
        long size = 0;
        try { if (output is not null && File.Exists(output)) size = new FileInfo(output).Length; } catch (IOException) { }
        return template
            .Replace("{source}", Q(job?.SourcePath))
            .Replace("{name}", Q(job is null ? "" : Path.GetFileNameWithoutExtension(job.SourcePath)))
            .Replace("{folder}", Q(job?.Folder))
            .Replace("{output}", Q(output))
            .Replace("{outputfolder}", Q(output is null ? null : Path.GetDirectoryName(output)))
            .Replace("{status}", job?.Status.ToString() ?? (failed > 0 ? "Error" : "Done"))
            .Replace("{message}", Q(job?.Message))
            .Replace("{duration}", ((int)(job?.Elapsed?.TotalSeconds ?? 0)).ToString(CultureInfo.InvariantCulture))
            .Replace("{size}", size.ToString(CultureInfo.InvariantCulture))
            .Replace("{done}", done.ToString(CultureInfo.InvariantCulture))
            .Replace("{failed}", failed.ToString(CultureInfo.InvariantCulture))
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", DateTime.Now.ToString("HH-mm-ss", CultureInfo.InvariantCulture));
    }

    public static string Quote(string v) => OperatingSystem.IsWindows()
        ? "\"" + v.Replace("\"", "\"\"") + "\""
        : "'" + v.Replace("'", "'\\''") + "'";

    /// <summary>Runs one command line with the system shell; returns its exit code and output (never throws).</summary>
    public static async Task<(int Exit, string Output)> RunAsync(string commandLine, string? workDir = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", commandLine } }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", commandLine } };
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            if (workDir is not null && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync(ct);
            var se = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(30));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { ProcessControl.KillTree(p); return (-1, "timeout"); }
            var text = ((await so) + (await se)).Trim();
            return (p.ExitCode, text.Length > 4000 ? text[..4000] + "…" : text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return (-1, ex.Message); }
    }
}
