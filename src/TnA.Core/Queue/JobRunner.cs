using System.Diagnostics;
using TnA.Core.Encoding;
using TnA.Core.Media;
using TnA.Core.Processing;
using TnA.Core.Settings;
using TnA.Core.Tools;

namespace TnA.Core.Queue;

public sealed record RunnerContext(
    FfmpegTools Tools,
    AppSettings Settings,
    SettingsStore Store,
    ProcessGroup Processes,
    Action<QueueJob> Changed);

/// <summary>Runs a single job end to end: probe → prepare subtitles/fonts → encode → log → clean up.</summary>
public static class JobRunner
{
    public static async Task RunAsync(QueueJob job, RunnerContext ctx, CancellationToken ct)
    {
        string workDir = Path.Combine(ctx.Store.TempDirectory, job.Id.ToString("N"));
        job.ResetForRun();
        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.Now;
        ctx.Changed(job);

        try
        {
            if (!File.Exists(job.SourcePath)) throw new FileNotFoundException("Source file not found.", job.SourcePath);

            job.Stage = "Analysing";
            ctx.Changed(job);
            job.Media = await new MediaProber(ctx.Tools.Ffprobe).ProbeAsync(job.SourcePath, ct);
            job.AppendLog($"Source: {job.SourcePath}");
            job.AppendLog($"Media: {job.Media.Summary}");

            var s = job.Settings;
            CheckEncoderAvailability(s, ctx.Settings.Capabilities);

            if (s.ProfileDefinition.IsKeyframes)
            {
                job.Stage = "Generating keyframes";
                ctx.Changed(job);
                job.OutputPath = await KeyframeGenerator.RunAsync(job, ctx, ct);
                Complete(job, JobStatus.Done, null, ctx);
                return;
            }

            Directory.CreateDirectory(workDir);
            PreparedSubtitles? subs = null;
            if (s.EffectiveSubtitles == SubtitleMode.Hardsub)
            {
                job.Stage = "Preparing subtitles";
                ctx.Changed(job);
                subs = await PrepareSubtitlesAsync(job, job.Media, s, workDir, ctx, ct);
            }

            var extra = SplitArgs(ctx.Settings.ExtraArguments);
            var plan = CommandBuilder.Build(job.Media, s, subs, new BuildOptions
            {
                OutputDirectory = ctx.Settings.OutputDirectory,
                VaapiDevice = ctx.Settings.VaapiDevice,
                ExtraArguments = extra,
                AppVersion = AppInfo.Version,
            });
            job.OutputPath = plan.OutputPath;
            job.CommandLine = plan.CommandLine(ctx.Tools.Ffmpeg);
            job.AppendLog("");
            job.AppendLog("Command: " + job.CommandLine);
            job.AppendLog("");
            Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputPath)!);

            job.Stage = s.ProfileDefinition.IsRemux ? "Remuxing" : "Encoding";
            ctx.Changed(job);

            var parser = new ProgressParser(job.Media.Duration);
            var lastNotify = DateTime.MinValue;
            int exit = await RunStreamingAsync(ctx.Tools.Ffmpeg, plan.Arguments, workDir, ctx.Processes,
                onStdout: line =>
                {
                    if (parser.Feed(line) is { } p)
                    {
                        job.Progress = p;
                        if ((DateTime.UtcNow - lastNotify).TotalMilliseconds > 250 || p.Finished)
                        {
                            lastNotify = DateTime.UtcNow;
                            ctx.Changed(job);
                        }
                    }
                },
                onStderr: line => job.AppendLog(line), ct);

            if (exit == 0)
            {
                var warn = job.Log.Contains("Glyph 0x", StringComparison.Ordinal) ? "Some glyphs were missing from the fonts." : null;
                Complete(job, warn is null ? JobStatus.Done : JobStatus.Warning, warn, ctx);
            }
            else
            {
                DeletePartial(job.OutputPath);
                Complete(job, JobStatus.Error, LastError(job.Log) ?? $"FFmpeg exited with code {exit}.", ctx);
            }
        }
        catch (OperationCanceledException)
        {
            DeletePartial(job.OutputPath);
            Complete(job, JobStatus.Stopped, "Stopped by user.", ctx);
        }
        catch (Exception ex)
        {
            DeletePartial(job.OutputPath);
            job.AppendLog("ERROR: " + ex);
            Complete(job, JobStatus.Error, ex.Message, ctx);
        }
        finally
        {
            TryDeleteDirectory(workDir);
            WriteLogFile(job, ctx.Store);
        }
    }

    private static void CheckEncoderAvailability(JobSettings s, FfmpegCapabilities? caps)
    {
        var p = s.ProfileDefinition;
        if (!p.EncodesVideo && p.Kind != ProfileKind.RemuxMp4) return;
        if (caps is null) return; // not detected yet: let FFmpeg decide.
        var codec = p.Kind == ProfileKind.RemuxMp4 ? VideoCodec.H264 : s.EffectiveCodec;
        bool tenBit = p.Kind != ProfileKind.RemuxMp4 && s.EffectiveTenBit;
        if (caps.Find(codec, s.EffectiveBackend) is { } cap && !(tenBit ? cap.Works10Bit : cap.Works8Bit))
            throw new InvalidOperationException(
                $"{cap.Encoder}{(tenBit ? " (10-bit)" : "")} did not pass the hardware test on this machine. " +
                "Pick another encoder or re-run the hardware detection.");
    }

    private static async Task<PreparedSubtitles?> PrepareSubtitlesAsync(QueueJob job, MediaInfo media, JobSettings s,
        string workDir, RunnerContext ctx, CancellationToken ct)
    {
        var choice = SubtitleSelector.Choose(media, s.UseSidecarSubtitles);
        if (choice is null)
        {
            job.AppendLog("Hardsub: no subtitles found, encoding without them.");
            return null;
        }
        job.AppendLog("Hardsub: using " + choice.Description);

        if (choice.IsBitmap) return new PreparedSubtitles(null, null, choice.Stream!.Index);

        string? fontsDir = null;
        if (media.Attachments.Any(a => a.IsFont))
        {
            var fonts = Path.Combine(workDir, "fonts");
            Directory.CreateDirectory(fonts);
            // Dumps every attachment using its own file name into the fonts folder.
            var r = await ProcessRunner.RunAsync(ctx.Tools.Ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-dump_attachment:t", "", "-i", media.Path, "-t", "0", "-f", "null", "-"],
                workingDirectory: fonts, ct: ct);
            int count = Directory.GetFiles(fonts).Length;
            job.AppendLog($"Fonts: {count} attachment(s) extracted.");
            if (count == 0 && r.ExitCode != 0) job.AppendLog(r.StdErr);
            fontsDir = "fonts";
        }

        string subFile;
        if (choice.ExternalFile is { } ext)
        {
            subFile = "sub" + Path.GetExtension(ext).ToLowerInvariant();
            File.Copy(ext, Path.Combine(workDir, subFile), overwrite: true);
        }
        else
        {
            subFile = "sub.ass";
            var st = choice.Stream!;
            var r = await ProcessRunner.RunAsync(ctx.Tools.Ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-i", media.Path, "-map", $"0:{st.Index}", "-c:s", st.IsAss ? "copy" : "ass", subFile],
                workingDirectory: workDir, ct: ct);
            if (r.ExitCode != 0 || !File.Exists(Path.Combine(workDir, subFile)))
                throw new InvalidOperationException("Could not extract the subtitle track: " + LastError(r.StdErr));
        }
        return new PreparedSubtitles(subFile, fontsDir, null);
    }

    internal static async Task<int> RunStreamingAsync(string exe, IEnumerable<string> args, string? workDir,
        ProcessGroup group, Action<string>? onStdout, Action<string>? onStderr, CancellationToken ct)
    {
        using var p = new Process { StartInfo = ProcessRunner.CreateStartInfo(exe, args, workDir), EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderr?.Invoke(e.Data); };
        p.Start();
        group.Add(p);
        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var reg = ct.Register(() => ProcessControl.KillTree(p));
            await p.WaitForExitAsync(CancellationToken.None);
            p.WaitForExit(); // flush async readers
            ct.ThrowIfCancellationRequested();
            return p.ExitCode;
        }
        finally
        {
            group.Remove(p);
        }
    }

    private static void Complete(QueueJob job, JobStatus status, string? message, RunnerContext ctx)
    {
        job.Status = status;
        job.Message = message;
        job.Stage = null;
        job.FinishedAt = DateTimeOffset.Now;
        job.AppendLog($"Result: {status}{(message is null ? "" : " - " + message)}");
        ctx.Changed(job);
    }

    internal static string? LastError(string log) =>
        log.Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0 && !ProgressParser.IsProgressLine(l) && !l.StartsWith("Result:", StringComparison.Ordinal)
                                && (l.Contains("rror", StringComparison.Ordinal) || l.Contains("nvalid", StringComparison.Ordinal)
                                    || l.Contains("ailed", StringComparison.Ordinal) || l.Contains("not ", StringComparison.Ordinal)));

    internal static List<string> SplitArgs(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    private static void DeletePartial(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void WriteLogFile(QueueJob job, SettingsStore store)
    {
        try
        {
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss} {Path.GetFileNameWithoutExtension(job.SourcePath)}.log";
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            File.WriteAllText(Path.Combine(store.LogsDirectory, name), job.Log);
        }
        catch (IOException) { }
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.0.0";
}
