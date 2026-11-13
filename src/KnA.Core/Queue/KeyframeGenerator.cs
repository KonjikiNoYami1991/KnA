using System.Diagnostics;
using System.Globalization;
using System.Text;
using KnA.Core.Processing;

namespace KnA.Core.Queue;

/// <summary>
/// Produces an Aegisub keyframe file. With SCXviD available the result matches the classic fansub workflow
/// (ffmpeg → y4m → scxvid, piped in-process: pausing now pauses both programs, unlike the old cmd.exe pipe).
/// Without it, FFmpeg scene-change detection writes the "keyframe format v1" list.
/// </summary>
public static class KeyframeGenerator
{
    public static async Task<string> RunAsync(QueueJob job, RunnerContext ctx, CancellationToken ct)
    {
        var media = job.Media!;
        var dir = string.IsNullOrWhiteSpace(ctx.Settings.OutputDirectory) ? job.Folder : ctx.Settings.OutputDirectory!;
        var output = Path.Combine(dir, Path.GetFileNameWithoutExtension(job.SourcePath) + " - Keyframes.txt");
        for (int i = 2; File.Exists(output); i++)
            output = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(job.SourcePath)} - Keyframes ({i}).txt");

        var parser = new ProgressParser(media.Duration);
        void OnStderr(string line)
        {
            if (parser.Feed(line) is { } p) { job.Progress = p; ctx.Changed(job); }
            else if (!ProgressParser.IsProgressLine(line)) job.AppendLog(line);
        }

        var v = media.PrimaryVideo!;
        if (ctx.Tools.Scxvid is { } scxvid)
        {
            job.AppendLog("Keyframes: SCXviD " + scxvid);
            await RunScxvidAsync(ctx, media.Path, v.Index, scxvid, output, OnStderr, ct);
        }
        else
        {
            job.AppendLog("Keyframes: SCXviD not found, using FFmpeg scene detection (threshold 0.3).");
            var work = Path.Combine(ctx.Store.TempDirectory, job.Id.ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                int exit = await JobRunner.RunStreamingAsync(ctx.Tools.Ffmpeg,
                    ["-hide_banner", "-nostdin", "-v", "error", "-i", media.Path, "-map", $"0:{v.Index}", "-an", "-sn",
                     "-vf", "select='gt(scene,0.3)',metadata=print:file=scenes.txt", "-f", "null", "-",
                     "-progress", "pipe:2", "-nostats"],
                    work, ctx.Processes, null, OnStderr, ct);
                if (exit != 0) throw new InvalidOperationException($"FFmpeg exited with code {exit}.");
                var scenes = Path.Combine(work, "scenes.txt");
                var frames = ParseSceneFile(File.Exists(scenes) ? File.ReadAllText(scenes) : "", v.FrameRate);
                await File.WriteAllTextAsync(output, FormatAegisub(frames, v.FrameRate), ct);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { }
            }
        }
        job.AppendLog("Keyframes written to " + output);
        return output;
    }

    private static async Task RunScxvidAsync(RunnerContext ctx, string source, int videoIndex, string scxvid,
        string output, Action<string> onStderr, CancellationToken ct)
    {
        var ffPsi = ProcessRunner.CreateStartInfo(ctx.Tools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-i", source, "-map", $"0:{videoIndex}", "-an", "-sn",
             "-pix_fmt", "yuv420p", "-f", "yuv4mpegpipe", "-progress", "pipe:2", "-nostats", "-"]);
        var scPsi = ProcessRunner.CreateStartInfo(scxvid, [output]);
        scPsi.RedirectStandardInput = true;

        using var ff = new Process { StartInfo = ffPsi };
        using var sc = new Process { StartInfo = scPsi };
        ff.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderr(e.Data); };
        sc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderr(e.Data); };
        sc.Start();
        ff.Start();
        ctx.Processes.Add(ff);
        ctx.Processes.Add(sc);
        try
        {
            ff.BeginErrorReadLine();
            sc.BeginErrorReadLine();
            sc.BeginOutputReadLine();
            using var reg = ct.Register(() => { ProcessControl.KillTree(ff); ProcessControl.KillTree(sc); });
            try
            {
                await ff.StandardOutput.BaseStream.CopyToAsync(sc.StandardInput.BaseStream, 1 << 20, CancellationToken.None);
            }
            catch (IOException) { /* scxvid closed the pipe */ }
            sc.StandardInput.Close();
            await ff.WaitForExitAsync(CancellationToken.None);
            await sc.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (ff.ExitCode != 0) throw new InvalidOperationException($"FFmpeg exited with code {ff.ExitCode}.");
            if (sc.ExitCode != 0) throw new InvalidOperationException($"SCXviD exited with code {sc.ExitCode}.");
        }
        finally
        {
            ctx.Processes.Remove(ff);
            ctx.Processes.Remove(sc);
        }
    }

    /// <summary>Reads the metadata filter output ("frame:N pts:X pts_time:T" lines) into source frame numbers.</summary>
    internal static List<long> ParseSceneFile(string text, double fps)
    {
        var frames = new SortedSet<long> { 0 };
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (i < 0) continue;
            var value = line[(i + 9)..].Trim().Split(' ', '\t')[0];
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && fps > 0)
                frames.Add((long)Math.Round(t * fps));
        }
        return [.. frames];
    }

    internal static string FormatAegisub(IEnumerable<long> frames, double fps)
    {
        var sb = new StringBuilder();
        sb.Append("# keyframe format v1\n");
        sb.Append("fps ").Append(fps > 0 ? fps.ToString("0.######", CultureInfo.InvariantCulture) : "0").Append('\n');
        foreach (var f in frames) sb.Append(f.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }
}
