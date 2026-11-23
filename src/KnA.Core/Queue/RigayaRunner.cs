using System.Diagnostics;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Processing;

namespace KnA.Core.Queue;

/// <summary>
/// Video encode with NVEncC / QSVEncC / VCEEncC:
///  • native: the encoder opens the source with GPU decoding (--avhw), resizes on the GPU when needed and copies
///    Dolby Vision / HDR10+ (the StaxRip way: decode → encode never leaves the GPU);
///  • pipe: FFmpeg decodes, filters, burns subtitles and trims exactly as in a single-pass encode and sends y4m
///    frames to the encoder's stdin (a VapourSynth or AI-upscaler process can sit in front of FFmpeg).
/// If the native path fails (a format the GPU reader rejects), the pipe path runs instead. Audio, subtitles,
/// attachments and chapters are made meanwhile by FFmpeg and muxed with the video at the end.
/// </summary>
public static class RigayaRunner
{
    public static async Task<int> RunAsync(QueueJob job, RunnerContext ctx, CompileContext baseCtx, CompiledJob final, string workDir, CancellationToken ct)
    {
        var plan = final.Rigaya!;
        var name = RigayaEncoders.DisplayName(plan.Backend);
        var exe = baseCtx.Options.RigayaEncoders.GetValueOrDefault(plan.Backend)
                  ?? throw new InvalidOperationException(L.T("{0} is not installed: add it in Settings → Components, or choose FFmpeg as the encoder program.", name));
        var media = job.Media!;
        var videoTrack = final.Tracks.First(t => t.Include && t.Kind == "video");
        var videoMedia = job.ExternalMedia.GetValueOrDefault(videoTrack.File) ?? media;
        double frameRate = job.Settings.VideoFilters.Fps ?? videoMedia.PrimaryVideo?.FrameRate ?? 25;

        var dir = Path.Combine(WorkFolders.Chunks(job, ctx.Store), "x-" + ChunkedEncoder.Key(job)[..16]);
        Directory.CreateDirectory(dir);
        var video = Path.Combine(dir, "video.mkv");
        TryDelete(video);
        var restTask = ChunkedEncoder.BuildRestAsync(job, ctx, baseCtx, dir, workDir, ct);

        job.BeginStage(L.T("Encoding ({0})", name));
        ctx.Changed(job);
        int exit = -1;
        double start = 0;
        if (plan.NativeArgs is { } native)
        {
            var args = native.ToList();
            args[^1] = video;
            job.CommandLine = new EncodePlan(args, video).CommandLine(exe);
            job.AppendLog($"Command ({name}, GPU reader): " + job.CommandLine);
            exit = await RunChainAsync([(exe, args, "")], workDir, ctx,
                line => Progress(job, ctx, RigayaEncoders.ParseProgress(line, final.OutputDuration, frameRate)), job, ct);
            if (exit != 0)
            {
                ct.ThrowIfCancellationRequested();
                job.AppendLog(L.T("{0} could not read the source itself (exit code {1}): FFmpeg decodes it instead.", name, exit));
                TryDelete(video);
            }
        }
        else job.AppendLog($"{name}: FFmpeg decodes the video ({plan.PipeReason}).");

        if (exit != 0)
        {
            double delay = videoTrack.DelaySeconds;
            start = Math.Max(job.Edit.TrimStart ?? 0, Math.Max(0, delay));
            double end = Math.Min(job.Edit.TrimEnd is > 0 and var te ? te : double.MaxValue, videoMedia.Duration.TotalSeconds + delay);
            var y4m = CommandBuilder.Compile(baseCtx with { Chunk = new ChunkRequest(start, end) { Y4m = true } });
            if (y4m.HasErrors) throw new InvalidOperationException(y4m.Issues.First(x => x.IsError).Message);
            var encArgs = RigayaEncoders.PipeArgs(plan.EncoderArgs, video);
            var chain = new List<(string, IReadOnlyList<string>, string)>();
            if (y4m.Pipe is { } src)
                chain.Add(src.Command is { Count: > 0 } cmd
                    ? (cmd[0], cmd.Skip(1).ToList(), "[kna-ai] ")
                    : (ctx.Tools.VsPipe ?? throw new InvalidOperationException(L.T("vspipe was not found: install VapourSynth (or set KNA_VSPIPE).")),
                        Frameserver.VsPipeVideoArgs(src.Script, src.StartFrame, src.EndFrame), "[vspipe] "));
            chain.Add((ctx.Tools.Ffmpeg, y4m.Plan.Arguments.ToList(), "[ffmpeg] "));
            chain.Add((exe, encArgs, ""));
            job.CommandLine = y4m.Plan.CommandLine(ctx.Tools.Ffmpeg) + " | " + new EncodePlan(encArgs, video).CommandLine(exe);
            job.AppendLog($"Command ({name}, FFmpeg pipe): " + job.CommandLine);
            var parser = new ProgressParser(y4m.OutputDuration);
            exit = await RunChainAsync(chain, workDir, ctx, line =>
            {
                // FFmpeg's total_size is the raw y4m stream sent to the encoder (~600 Mb/s at 1080p): the size shown
                // (and the final size estimated from it) must be the encoded file the encoder is writing.
                if (line.StartsWith("[ffmpeg] ", StringComparison.Ordinal) && parser.Feed(line[9..]) is { } pr)
                    Progress(job, ctx, pr with { SizeBytes = FileSize(video) });
            }, job, ct);
        }

        if (exit != 0 || !File.Exists(video) || new FileInfo(video).Length == 0)
        {
            try { await restTask; } catch (Exception) { /* the encode error is the one to report */ }
            return exit != 0 ? exit : -1;
        }
        int code = await ChunkedEncoder.AssembleAsync(job, ctx, baseCtx, final, dir, [video], start, workDir, ct, restTask);
        if (code == 0)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        return code;
    }

    private static void Progress(QueueJob job, RunnerContext ctx, EncodeProgress? p)
    {
        if (p is null) return;
        job.Progress = p;
        ctx.Changed(job);
    }

    /// <summary>
    /// Runs processes connected stdout → stdin (the last one's stdout is discarded). Every stderr line goes to
    /// <paramref name="onLine"/> with the step's prefix; lines that aren't progress go to the job log.
    /// </summary>
    internal static async Task<int> RunChainAsync(IReadOnlyList<(string Exe, IReadOnlyList<string> Args, string Prefix)> steps, string workDir,
        RunnerContext ctx, Action<string> onLine, QueueJob job, CancellationToken ct)
    {
        var procs = new List<Process>();
        var copies = new List<Task>();
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                var (exe, args, prefix) = steps[i];
                var psi = ProcessRunner.CreateStartInfo(exe, args, workDir);
                psi.RedirectStandardInput = true;
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                bool last = i == steps.Count - 1;
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not { Length: > 0 } d) return;
                    var line = prefix + d;
                    onLine(line);
                    if (!ProgressParser.IsProgressLine(d) && RigayaEncoders.ParseProgress(d, TimeSpan.Zero, 0) is null)
                        lock (job) job.AppendLog(line);
                };
                if (last) p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) lock (job) job.AppendLog(prefix + d); };
                p.Start();
                JobRunner.SetPriority(p, ctx.Settings.Priority);
                ctx.Processes.Add(p);
                procs.Add(p);
                p.BeginErrorReadLine();
                if (last) p.BeginOutputReadLine();
                if (i == 0) p.StandardInput.Close();
                else
                {
                    var from = procs[i - 1];
                    var to = p;
                    copies.Add(Task.Run(async () =>
                    {
                        try { await from.StandardOutput.BaseStream.CopyToAsync(to.StandardInput.BaseStream, 1 << 22); }
                        catch (IOException) { /* the reader stopped */ }
                        catch (ObjectDisposedException) { }
                        finally { try { to.StandardInput.Close(); } catch (IOException) { } }
                    }));
                }
            }
            using var reg = ct.Register(() => { foreach (var p in procs) ProcessControl.KillTree(p); });
            var lastProc = procs[^1];
            await lastProc.WaitForExitAsync(CancellationToken.None);
            lastProc.WaitForExit();
            // The encoder stopped: the producers have nobody to write to.
            var killed = new HashSet<Process>();
            foreach (var p in procs.Take(procs.Count - 1))
                if (!p.HasExited && !p.WaitForExit(lastProc.ExitCode == 0 ? 5000 : 0)) { killed.Add(p); ProcessControl.KillTree(p); }
            await Task.WhenAll(copies);
            ct.ThrowIfCancellationRequested();
            // FFmpeg failing (the step that feeds the encoder) leaves the video incomplete even if the encoder ended cleanly.
            if (lastProc.ExitCode == 0 && procs.Count >= 2 && procs[^2] is var feeder && !killed.Contains(feeder) && feeder.ExitCode != 0)
                return feeder.ExitCode;
            return lastProc.ExitCode;
        }
        finally
        {
            foreach (var p in procs) { ctx.Processes.Remove(p); p.Dispose(); }
        }
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
