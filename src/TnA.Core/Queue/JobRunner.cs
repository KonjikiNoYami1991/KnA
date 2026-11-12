using System.Diagnostics;
using TnA.Core.Encoding;
using TnA.Core.Localization;
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

/// <summary>Runs a single job end to end: probe → validate → prepare → encode → verify → log → clean up.</summary>
public static class JobRunner
{
    public static async Task RunAsync(QueueJob job, RunnerContext ctx, CancellationToken ct)
    {
        string workDir = Path.Combine(ctx.Store.TempDirectory, job.Id.ToString("N"));
        job.ResetForRun();
        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.Now;
        ctx.Changed(job);
        string? createdFolder = null;

        try
        {
            if (!File.Exists(job.SourcePath)) throw new FileNotFoundException(L.T("Source file not found."), job.SourcePath);

            job.Stage = L.T("Analysing");
            ctx.Changed(job);
            var prober = MediaProber.For(ctx.Tools);
            job.Media = await prober.ProbeAsync(job.SourcePath, ct);
            EnsureFrameserverScript(job, ctx.Store);
            await ProbeExternalsAsync(job, prober, ct);
            job.AppendLog($"TnA {AppInfo.Version} · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            job.AppendLog($"Source: {job.SourcePath}");
            job.AppendLog($"Media: {job.Media.Summary}");
            if (job.Media.Ignored.Count > 0)
                job.AppendLog("Ignored streams: " + string.Join(", ", job.Media.Ignored.Select(s => $"#{s.Index} {s.Kind} {s.CodecName}")));
            foreach (var ext in job.Edit.ExternalFiles) job.AppendLog("External: " + ext);

            var s = job.Settings;
            var p = s.ProfileDefinition;
            CheckEncoderAvailability(s, ctx.Settings.Capabilities);

            if (p.IsKeyframes)
            {
                job.Stage = L.T("Generating keyframes");
                ctx.Changed(job);
                job.OutputPath = await KeyframeGenerator.RunAsync(job, ctx, ct);
                Complete(job, JobStatus.Done, null, ctx);
                return;
            }

            if (p.IsDemux)
            {
                createdFolder = await RunDemuxAsync(job, ctx, ct);
                Complete(job, JobStatus.Done, null, ctx);
                return;
            }

            // ------------------------------------------------------------ validate
            var baseCtx = BuildContext(job, ctx);
            var check = CommandBuilder.Compile(baseCtx);
            job.Issues = check.Issues;
            job.HdrSummary = check.Hdr.Summary;
            foreach (var i in check.Issues) job.AppendLog((i.IsError ? "ERROR: " : "Warning: ") + i.Message);
            if (check.HasErrors) throw new InvalidOperationException(check.Issues.First(i => i.IsError).Message);
            job.AppendLog("HDR: " + check.Hdr.Summary);

            CheckDestination(p.IsImages ? Path.GetDirectoryName(check.Plan.OutputPath)! : Path.GetDirectoryName(check.Plan.OutputPath)!, job.Media);

            // ------------------------------------------------------------ prepare
            Directory.CreateDirectory(workDir);
            PreparedSubtitles? subs = null;
            var burnTrack = check.Tracks.FirstOrDefault(t => t.Burn && t.Kind == "subtitle");
            if (burnTrack is not null && p.UsesVideoFilters)
            {
                job.Stage = L.T("Preparing subtitles");
                ctx.Changed(job);
                subs = await PrepareSubtitlesAsync(job, burnTrack, check.Tracks, workDir, ctx, ct);
            }

            HdrSideFiles? sideFiles = null;
            if (check.Hdr.UseDolbyVisionRpu || check.Hdr.UseHdr10PlusJson)
            {
                job.Stage = L.T("Extracting HDR metadata");
                ctx.Changed(job);
                sideFiles = await ExtractHdrMetadataAsync(job, check.Hdr, workDir, ctx, ct);
            }

            var scriptAudio = await RenderScriptAudioAsync(check.Tracks, workDir, ctx, job, ct);
            baseCtx = baseCtx with { ScriptAudio = scriptAudio };
            var compiled = CommandBuilder.Compile(baseCtx with { Burn = subs, HdrFiles = sideFiles });
            if (compiled.HasErrors) throw new InvalidOperationException(compiled.Issues.First(i => i.IsError).Message);
            job.OutputPath = compiled.Plan.OutputPath;
            job.CommandLine = compiled.Plan.CommandLine(ctx.Tools.Ffmpeg);
            job.AppendLog("");
            job.AppendLog("Command: " + job.CommandLine);
            job.AppendLog("");
            if (p.IsImages)
            {
                Directory.CreateDirectory(compiled.Plan.OutputPath);
                createdFolder = compiled.Plan.OutputPath;
            }
            else Directory.CreateDirectory(Path.GetDirectoryName(compiled.Plan.OutputPath)!);

            // ------------------------------------------------------------ run
            job.Stage = p.IsRemux ? L.T("Remuxing") : p.IsImages ? L.T("Exporting images") : L.T("Encoding");
            ctx.Changed(job);
            int exit = s.Chunks.Enabled && p.EncodesVideo
                ? await ChunkedEncoder.RunAsync(job, ctx, baseCtx with { Burn = subs, HdrFiles = sideFiles }, compiled, workDir, ct)
                : await RunFfmpegWithProgressAsync(job, compiled.Plan.Arguments, workDir, compiled.OutputDuration, ctx, ct, compiled.Pipe);

            if (exit != 0)
            {
                Cleanup(job.OutputPath, createdFolder);
                Complete(job, JobStatus.Error, LastError(job.Log) ?? L.T("FFmpeg exited with code {0}.", exit), ctx);
                return;
            }

            // ------------------------------------------------------------ verify
            if (p.IsImages)
            {
                int n = Directory.Exists(createdFolder) ? Directory.GetFiles(createdFolder).Length : 0;
                if (n == 0) throw new InvalidOperationException(L.T("No image was written."));
                job.AppendLog($"{n} images written.");
            }
            else if (!File.Exists(job.OutputPath) || new FileInfo(job.OutputPath).Length == 0)
                throw new InvalidOperationException(L.T("FFmpeg finished but the output file is empty."));

            var warnings = new List<string>();
            if (job.Log.Contains("Glyph 0x", StringComparison.Ordinal)) warnings.Add(L.T("Some glyphs were missing from the fonts."));
            warnings.AddRange(compiled.Issues.Where(i => !i.IsError).Select(i => i.Message));
            Complete(job, warnings.Count == 0 ? JobStatus.Done : JobStatus.Warning, warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct()), ctx);
        }
        catch (OperationCanceledException)
        {
            Cleanup(job.OutputPath, createdFolder);
            Complete(job, JobStatus.Stopped, L.T("Stopped by user."), ctx);
        }
        catch (Exception ex)
        {
            Cleanup(job.Status == JobStatus.Running ? job.OutputPath : null, createdFolder);
            job.AppendLog("ERROR: " + ex);
            Complete(job, JobStatus.Error, ex.Message, ctx);
        }
        finally
        {
            TryDeleteDirectory(workDir);
            WriteLogFile(job, ctx.Store);
        }
    }

    public static CompileContext BuildContext(QueueJob job, RunnerContext ctx) => BuildContext(job, ctx.Settings, ctx.Tools);

    public static CompileContext BuildContext(QueueJob job, AppSettings settings, FfmpegTools? tools)
    {
        var media = new Dictionary<string, MediaInfo>(job.ExternalMedia) { [job.SourcePath] = job.Media! };
        var caps = settings.Capabilities;
        var edit = job.Edit.Tracks is null && FrameserverMedia(job) is { } fs
            ? job.Edit with { Tracks = TrackPlanner.Default(job.Media!, job.Settings, frameserver: fs) }
            : job.Edit;
        return new CompileContext
        {
            Source = job.Media!,
            Media = media,
            Settings = job.Settings,
            Edit = edit,
            Options = new BuildOptions
            {
                OutputDirectory = settings.OutputDirectory,
                NameTemplate = settings.OutputNameTemplate,
                VaapiDevice = settings.VaapiDevice,
                ExtraArguments = SplitArgs(settings.ExtraArguments),
                AppVersion = AppInfo.Version,
            },
            Hdr = new HdrEnvironment(caps?.HasZscale ?? true, caps?.HasLibplacebo ?? false, tools?.DoviTool is not null, tools?.Hdr10PlusTool is not null),
        };
    }

    /// <summary>Writes the job's VapourSynth/AviSynth script (Frameserver settings) and remembers its path.</summary>
    internal static void EnsureFrameserverScript(QueueJob job, SettingsStore store)
    {
        var fs = job.Settings.Frameserver;
        if (fs.Kind == FrameserverKind.None || string.IsNullOrWhiteSpace(fs.Script) || Frameserver.KindOf(job.SourcePath) != FrameserverKind.None
            || !job.Settings.ProfileDefinition.UsesVideoFilters)
        {
            job.FrameserverScript = null;
            return;
        }
        job.FrameserverScript = Frameserver.WriteScript(Path.Combine(store.DataDirectory, "scripts"), job.Id, fs, job.SourcePath);
    }

    internal static MediaInfo? FrameserverMedia(QueueJob job) =>
        job.FrameserverScript is { } p ? job.ExternalMedia.GetValueOrDefault(p) : null;

    internal static async Task ProbeExternalsAsync(QueueJob job, MediaProber prober, CancellationToken ct)
    {
        if (job.FrameserverScript is { } script)
        {
            job.ExternalMedia.Remove(script); // the script text may have changed
            job.ExternalMedia[script] = await prober.ProbeAsync(script, ct, requireVideo: true);
        }
        foreach (var file in job.Edit.ExternalFiles.Concat(job.Edit.Tracks?.Select(t => t.File) ?? []).Distinct())
        {
            if (file == job.SourcePath || TrackPlanner.IsFontFile(file) || job.ExternalMedia.ContainsKey(file)) continue;
            if (!File.Exists(file)) throw new FileNotFoundException(L.T("External file not found: {0}", file), file);
            job.ExternalMedia[file] = await prober.ProbeAsync(file, ct, requireVideo: false);
        }
    }

    /// <summary>Fails early when the destination is not writable or clearly too full.</summary>
    internal static void CheckDestination(string folder, MediaInfo media)
    {
        Directory.CreateDirectory(folder);
        var probe = Path.Combine(folder, $".tna-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(L.T("Cannot write to {0}: {1}", folder, ex.Message));
        }
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            long need = Math.Max(256L << 20, Math.Min(media.SizeBytes / 4, 4L << 30));
            if (drive.IsReady && drive.AvailableFreeSpace < need)
                throw new InvalidOperationException(L.T("Not enough free space in {0} ({1} MB left).", folder, drive.AvailableFreeSpace >> 20));
        }
        catch (ArgumentException) { /* network paths without drive info */ }
    }

    private static async Task<string> RunDemuxAsync(QueueJob job, RunnerContext ctx, CancellationToken ct)
    {
        var plan = DemuxBuilder.Build(job.Media!, job.Settings, job.Edit, new BuildOptions { OutputDirectory = ctx.Settings.OutputDirectory });
        job.Issues = plan.Issues;
        if (plan.Issues.FirstOrDefault(i => i.IsError) is { } err) throw new InvalidOperationException(err.Message);
        CheckDestination(Path.GetDirectoryName(plan.Folder)!, job.Media!);
        Directory.CreateDirectory(plan.Folder);
        job.OutputPath = plan.Folder;
        job.CommandLine = plan.Plan.CommandLine(ctx.Tools.Ffmpeg);
        job.AppendLog("Command: " + job.CommandLine);
        foreach (var o in plan.Outputs) job.AppendLog($"  {DemuxBuilder.Describe(o)} → {o.FileName}");
        job.Stage = L.T("Extracting streams");
        ctx.Changed(job);
        var args = plan.Plan.Arguments.ToList();
        int pos = args.LastIndexOf("-i");
        args.InsertRange(pos + 2, CommandBuilder.ProgressArgs);
        var duration = job.Edit.IsTrimmed
            ? TimeSpan.FromSeconds((job.Edit.TrimEnd ?? job.Media!.Duration.TotalSeconds) - (job.Edit.TrimStart ?? 0))
            : job.Media!.Duration;
        // Attachments are dumped in the working directory → the destination folder.
        int exit = await RunFfmpegWithProgressAsync(job, args, plan.Folder, duration, ctx, ct);
        if (exit != 0) throw new InvalidOperationException(LastError(job.Log) ?? L.T("FFmpeg exited with code {0}.", exit));
        job.AppendLog($"{Directory.GetFiles(plan.Folder).Length} files written to {plan.Folder}");
        return plan.Folder;
    }

    private static async Task<int> RunFfmpegWithProgressAsync(QueueJob job, IReadOnlyList<string> args, string workDir,
        TimeSpan duration, RunnerContext ctx, CancellationToken ct, PipeSource? pipe = null)
    {
        var parser = new ProgressParser(duration);
        var lastNotify = DateTime.MinValue;
        var lastProgress = DateTime.UtcNow;
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dog = Task.Run(async () =>
        {
            while (!watchdog.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), watchdog.Token); } catch (OperationCanceledException) { return; }
                if (ctx.Processes.IsPaused) lastProgress = DateTime.UtcNow;
                else if (DateTime.UtcNow - lastProgress > TimeSpan.FromMinutes(Math.Max(1, ctx.Settings.StallTimeoutMinutes)))
                {
                    job.AppendLog($"ERROR: no progress for {ctx.Settings.StallTimeoutMinutes} minutes, FFmpeg looks stuck.");
                    ctx.Processes.KillAll();
                    return;
                }
            }
        });
        try
        {
            return await RunStreamingAsync(ctx.Tools.Ffmpeg, args, workDir, ctx.Processes, ctx.Settings.Priority, pipe, ctx.Tools.VsPipe,
                onStdout: line =>
                {
                    if (parser.Feed(line) is not { } p) return;
                    lastProgress = DateTime.UtcNow;
                    job.Progress = p;
                    if ((DateTime.UtcNow - lastNotify).TotalMilliseconds > 250 || p.Finished)
                    {
                        lastNotify = DateTime.UtcNow;
                        ctx.Changed(job);
                    }
                },
                onStderr: line => { lastProgress = DateTime.UtcNow; job.AppendLog(line); }, ct);
        }
        finally
        {
            await watchdog.CancelAsync();
            await dog;
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
            throw new InvalidOperationException(L.T(
                "{0}{1} did not pass the hardware test on this machine. Pick another encoder or re-run the hardware detection.",
                cap.Encoder, tenBit ? " (10-bit)" : ""));
    }

    /// <summary>
    /// Burned subtitles are extracted with their delay already applied (-itsoffset) and fonts are collected
    /// from every file of the plan (MKV attachments + standalone font files).
    /// </summary>
    internal static async Task<PreparedSubtitles?> PrepareSubtitlesAsync(QueueJob job, TrackPlan burn, IReadOnlyList<TrackPlan> tracks,
        string workDir, RunnerContext ctx, CancellationToken ct)
    {
        var media = new Dictionary<string, MediaInfo>(job.ExternalMedia) { [job.SourcePath] = job.Media! };
        var stream = TrackPlanner.Lookup(burn, media) as SubtitleStream;
        job.AppendLog($"Hardsub: {Path.GetFileName(burn.File)} #{burn.Stream} ({stream?.CodecName ?? burn.Codec}), delay {burn.DelayMs:0} ms");
        if (stream?.IsBitmap == true) return new PreparedSubtitles(null, null, stream.Index, burn.File, burn.DelaySeconds);

        string? fontsDir = await CollectFontsAsync(job, media, tracks, workDir, ctx, ct);

        const string subFile = "sub.ass";
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        args.AddRange(MediaProber.RobustInputArgs(burn.File));
        if (Math.Abs(burn.DelayMs) >= 0.5) args.AddRange(["-itsoffset", CommandBuilder.Sec(burn.DelaySeconds)]);
        args.AddRange(["-i", burn.File, "-map", $"0:{burn.Stream}", "-c:s", stream?.IsAss == true ? "copy" : "ass", subFile]);
        var r = await ProcessRunner.RunAsync(ctx.Tools.Ffmpeg, args, workingDirectory: workDir, timeout: TimeSpan.FromMinutes(5), ct: ct);
        if (r.ExitCode != 0 || !File.Exists(Path.Combine(workDir, subFile)))
            throw new InvalidOperationException(L.T("Could not extract the subtitle track: {0}", LastError(r.StdErr) ?? r.StdErr.Trim()));
        return new PreparedSubtitles(subFile, fontsDir, null);
    }

    internal static async Task<string?> CollectFontsAsync(QueueJob job, IReadOnlyDictionary<string, MediaInfo> media, IReadOnlyList<TrackPlan> tracks,
        string workDir, RunnerContext ctx, CancellationToken ct)
    {
        var fonts = Path.Combine(workDir, "fonts");
        foreach (var (path, m) in media)
        {
            if (!m.Attachments.Any(a => a.IsFont)) continue;
            Directory.CreateDirectory(fonts);
            await ProcessRunner.RunAsync(ctx.Tools.Ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-dump_attachment:t", "", "-i", path, "-t", "0", "-f", "null", "-"],
                workingDirectory: fonts, timeout: TimeSpan.FromMinutes(2), ct: ct);
        }
        foreach (var f in tracks.Where(t => t.Kind == "attachment" && t.Stream < 0 && File.Exists(t.File)).Select(t => t.File)
                     .Concat(job.Edit.ExternalFiles.Where(TrackPlanner.IsFontFile).Where(File.Exists)).Distinct())
        {
            Directory.CreateDirectory(fonts);
            File.Copy(f, Path.Combine(fonts, Path.GetFileName(f)), overwrite: true);
        }
        if (!Directory.Exists(fonts)) return null;
        job.AppendLog($"Fonts: {Directory.GetFiles(fonts).Length} available for libass.");
        return "fonts";
    }

    private static async Task<HdrSideFiles> ExtractHdrMetadataAsync(QueueJob job, HdrPlan hdr, string workDir, RunnerContext ctx, CancellationToken ct)
    {
        var v = job.Media!.PrimaryVideo!;
        string? rpu = null, json = null;
        if (hdr.UseDolbyVisionRpu && ctx.Tools.DoviTool is { } dovi)
        {
            rpu = Path.Combine(workDir, "RPU.bin");
            // Mode 2 converts profile 7 (dual layer) and keeps 8.x as profile 8.1.
            await PipeAsync(ctx, job.SourcePath, v.Index, dovi, ["-m", "2", "extract-rpu", "-", "-o", rpu], ct);
            job.AppendLog("Dolby Vision RPU extracted.");
        }
        if (hdr.UseHdr10PlusJson && ctx.Tools.Hdr10PlusTool is { } h10)
        {
            json = Path.Combine(workDir, "hdr10plus.json");
            await PipeAsync(ctx, job.SourcePath, v.Index, h10, ["extract", "-", "-o", json], ct);
            job.AppendLog("HDR10+ metadata extracted.");
        }
        return new HdrSideFiles(rpu, json);
    }

    /// <summary>ffmpeg (HEVC Annex B to stdout) | tool (stdin), both under pause/stop control.</summary>
    private static async Task PipeAsync(RunnerContext ctx, string source, int videoIndex, string tool, string[] toolArgs, CancellationToken ct)
    {
        var ffArgs = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        ffArgs.AddRange(MediaProber.RobustInputArgs(source));
        ffArgs.AddRange(["-i", source, "-map", $"0:{videoIndex}", "-c:v", "copy", "-bsf:v", "hevc_mp4toannexb", "-f", "hevc", "-"]);
        var toolPsi = ProcessRunner.CreateStartInfo(tool, toolArgs);
        toolPsi.RedirectStandardInput = true;
        using var ff = new Process { StartInfo = ProcessRunner.CreateStartInfo(ctx.Tools.Ffmpeg, ffArgs) };
        using var tp = new Process { StartInfo = toolPsi };
        var err = new System.Text.StringBuilder();
        tp.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (err) err.AppendLine(e.Data); };
        ff.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (err) err.AppendLine(e.Data); };
        tp.Start();
        ff.Start();
        ctx.Processes.Add(ff);
        ctx.Processes.Add(tp);
        try
        {
            tp.BeginErrorReadLine();
            tp.BeginOutputReadLine();
            ff.BeginErrorReadLine();
            using var reg = ct.Register(() => { ProcessControl.KillTree(ff); ProcessControl.KillTree(tp); });
            try { await ff.StandardOutput.BaseStream.CopyToAsync(tp.StandardInput.BaseStream, 1 << 20, CancellationToken.None); }
            catch (IOException) { }
            tp.StandardInput.Close();
            await ff.WaitForExitAsync(CancellationToken.None);
            await tp.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (tp.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(tool)}: {err.ToString().Trim()}");
        }
        finally
        {
            ctx.Processes.Remove(ff);
            ctx.Processes.Remove(tp);
        }
    }

    internal static Task<int> RunStreamingAsync(string exe, IEnumerable<string> args, string? workDir,
        ProcessGroup group, Action<string>? onStdout, Action<string>? onStderr, CancellationToken ct) =>
        RunStreamingAsync(exe, args, workDir, group, ProcessPriority.Normal, null, null, onStdout, onStderr, ct);

    internal static Task<int> RunStreamingAsync(string exe, IEnumerable<string> args, string? workDir,
        ProcessGroup group, ProcessPriority priority, Action<string>? onStdout, Action<string>? onStderr, CancellationToken ct) =>
        RunStreamingAsync(exe, args, workDir, group, priority, null, null, onStdout, onStderr, ct);

    /// <summary>Runs FFmpeg; with <paramref name="pipe"/>, vspipe feeds FFmpeg's stdin (both paused/stopped together).</summary>
    internal static async Task<int> RunStreamingAsync(string exe, IEnumerable<string> args, string? workDir,
        ProcessGroup group, ProcessPriority priority, PipeSource? pipe, string? vspipe,
        Action<string>? onStdout, Action<string>? onStderr, CancellationToken ct)
    {
        var psi = ProcessRunner.CreateStartInfo(exe, args, workDir);
        // Never inherit our stdin: without a pipe FFmpeg gets an immediate EOF instead of waiting forever.
        psi.RedirectStandardInput = true;
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onStdout?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderr?.Invoke(e.Data); };
        Process? vs = null;
        if (pipe is not null)
        {
            if (vspipe is null) throw new InvalidOperationException(L.T("vspipe was not found: install VapourSynth (or set TNA_VSPIPE)."));
            vs = new Process { StartInfo = ProcessRunner.CreateStartInfo(vspipe, Frameserver.VsPipeVideoArgs(pipe.Script, pipe.StartFrame, pipe.EndFrame), workDir) };
            vs.ErrorDataReceived += (_, e) => { if (e.Data is not null && !e.Data.StartsWith("Output ", StringComparison.Ordinal)) onStderr?.Invoke("[vspipe] " + e.Data); };
            vs.Start();
            SetPriority(vs, priority);
            group.Add(vs);
            vs.BeginErrorReadLine();
        }
        p.Start();
        SetPriority(p, priority);
        group.Add(p);
        if (vs is null) p.StandardInput.Close();
        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            using var reg = ct.Register(() => { ProcessControl.KillTree(p); if (vs is not null) ProcessControl.KillTree(vs); });
            Task copy = Task.CompletedTask;
            if (vs is not null)
                copy = Task.Run(async () =>
                {
                    try { await vs.StandardOutput.BaseStream.CopyToAsync(p.StandardInput.BaseStream, 1 << 20); }
                    catch (IOException) { /* ffmpeg stopped reading (e.g. -frames reached) */ }
                    finally { try { p.StandardInput.Close(); } catch (IOException) { } }
                });
            await p.WaitForExitAsync(CancellationToken.None);
            p.WaitForExit(); // flush async readers
            if (vs is not null)
            {
                if (!vs.HasExited) ProcessControl.KillTree(vs); // ffmpeg finished early (range end / frames limit)
                await copy;
            }
            ct.ThrowIfCancellationRequested();
            return p.ExitCode;
        }
        finally
        {
            group.Remove(p);
            if (vs is not null) { group.Remove(vs); vs.Dispose(); }
        }
    }

    /// <summary>Renders the audio outputs of VapourSynth scripts used by the plan to W64 files.</summary>
    internal static async Task<Dictionary<string, string>> RenderScriptAudioAsync(IReadOnlyList<TrackPlan> tracks, string workDir,
        RunnerContext ctx, QueueJob job, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        foreach (var t in tracks.Where(t => t.Include && t.Kind == "audio" && Frameserver.IsVapourSynth(t.File)))
        {
            if (ctx.Tools.VsPipe is null) throw new InvalidOperationException(L.T("vspipe was not found: install VapourSynth (or set TNA_VSPIPE)."));
            Directory.CreateDirectory(workDir);
            var file = Path.Combine(workDir, $"script-audio-{t.Stream}.w64");
            job.Stage = L.T("Rendering script audio");
            ctx.Changed(job);
            var r = await ProcessRunner.RunAsync(ctx.Tools.VsPipe, ["-o", t.Stream.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c", "w64", t.File, file],
                workingDirectory: workDir, timeout: TimeSpan.FromHours(6), ct: ct);
            if (r.ExitCode != 0) throw new InvalidOperationException(L.T("The VapourSynth script failed: {0}", r.StdErr.Trim()));
            map[$"{t.File}|{t.Stream}"] = file;
            job.AppendLog($"Script audio (output {t.Stream}) rendered.");
        }
        return map;
    }

    /// <summary>Lower priority keeps the computer responsive while encoding.</summary>
    internal static void SetPriority(Process p, ProcessPriority priority)
    {
        if (priority == ProcessPriority.Normal) return;
        try { p.PriorityClass = priority == ProcessPriority.Idle ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
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
                                && !l.StartsWith("Warning:", StringComparison.Ordinal)
                                && (l.Contains("rror", StringComparison.Ordinal) || l.Contains("nvalid", StringComparison.Ordinal)
                                    || l.Contains("ailed", StringComparison.Ordinal) || l.Contains("not ", StringComparison.Ordinal)
                                    || l.Contains("stuck", StringComparison.Ordinal)));

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

    /// <summary>Removes partial output (file or a folder TnA created for this job).</summary>
    private static void Cleanup(string? file, string? createdFolder)
    {
        try { if (file is not null && File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        TryDeleteDirectory(createdFolder);
    }

    private static void TryDeleteDirectory(string? dir)
    {
        try { if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void WriteLogFile(QueueJob job, SettingsStore store)
    {
        try
        {
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss} {Path.GetFileNameWithoutExtension(job.SourcePath)}.log";
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            File.WriteAllText(Path.Combine(store.LogsDirectory, name), job.Log);
            store.RotateLogs(store.KeepLogs);
        }
        catch (IOException) { }
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "2.0.0";
}
