using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Processing;
using KnA.Core.Settings;

namespace KnA.Core.Queue;

/// <summary>
/// av1an-style segmented encoding.
///  1. Split points: source keyframes (encoders put them on scene cuts) every ~TargetSeconds,
///     fixed intervals as a fallback. Times are in the FINAL timeline (video delay included).
///  2. Each segment is encoded on its own (video only) with the full filter chain and burned subtitles
///     on the original timeline (-copyts), then cut frame-exactly with trim=start:end. Segments run in parallel.
///  3. Finished segments are kept with a manifest: after a stop, a crash or a restart only the missing ones
///     are encoded again. Changing settings, tracks, trim or the source invalidates them.
///  4. Assembly: the segments are concatenated without re-encoding and muxed with audio, subtitles,
///     attachments and chapters; the user trim is still applied last.
/// </summary>
public static class ChunkedEncoder
{
    private sealed record Manifest(string Key, List<double> Bounds, List<bool> Done);

    /// <summary>
    /// A single encode that can still be resumed through segments (one worker) when the encoder can't write resumable
    /// pieces itself (see <see cref="ResumableEncoder"/>): never with dynamic HDR metadata or a copied video.
    /// </summary>
    public static bool CanResumeOneByOne(QueueJob job, CompiledJob final, AppSettings settings) =>
        settings.ResumableEncoding
        && job.Settings.ProfileDefinition.EncodesVideo
        && !KnA.Core.Media.Discs.DiscSource.IsTitlePath(job.SourcePath)
        && job.Media is { Duration.TotalSeconds: > 120 }
        && !job.Edit.IsMultiSegment
        && !final.Hdr.UseDolbyVisionRpu && !final.Hdr.UseHdr10PlusJson
        && final.Tracks.FirstOrDefault(t => t.Include && t.Kind == "video") is { CopyVideo: false };

    public static string ChunksRoot(SettingsStore store) => Path.Combine(store.DataDirectory, "chunks");

    /// <summary>Pause between the first starts of the workers, so the segments don't all end together (settable for tests).</summary>
    public static double StaggerSeconds { get; set; } = 4;

    /// <param name="chunks">Segment settings to use instead of the job's (one worker = resumable single encode).</param>
    public static async Task<int> RunAsync(QueueJob job, RunnerContext ctx, CompileContext baseCtx, CompiledJob final,
        string workDir, CancellationToken ct, ChunkSettings? chunks = null)
    {
        var s = job.Settings;
        var cs = chunks ?? s.Chunks;
        int workers = Math.Clamp(cs.Workers, 1, 32);
        var media = job.Media!;
        var tracks = final.Tracks;
        var videoTrack = tracks.First(t => t.Include && t.Kind == "video");
        var video = (VideoStream)TrackPlanner.Lookup(videoTrack, new Dictionary<string, MediaInfo>(job.ExternalMedia) { [job.SourcePath] = media })!;
        double delay = videoTrack.DelaySeconds;
        double videoDuration = (job.ExternalMedia.GetValueOrDefault(videoTrack.File) ?? media).Duration.TotalSeconds;
        double start = Math.Max(job.Edit.TrimStart ?? 0, Math.Max(0, delay));
        double end = Math.Min(job.Edit.TrimEnd is > 0 and var te ? te : double.MaxValue, videoDuration + delay);
        if (end - start < 1) throw new InvalidOperationException(L.T("The selected range is too short for chunked encoding."));

        // ------------------------------------------------------------ manifest
        var key = Key(job);
        var dir = Path.Combine(WorkFolders.Chunks(job, ctx.Store), key[..16]);
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, "manifest.json");
        Manifest manifest;
        if (File.Exists(manifestPath) && JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath)) is { } m && m.Key == key
            && m.Bounds.Count == m.Done.Count + 1)
        {
            manifest = m with { Done = m.Done.Select((d, i) => d && File.Exists(ChunkPath(dir, i))).ToList() };
            job.AppendLog($"Chunks: resuming, {manifest.Done.Count(d => d)}/{manifest.Done.Count} segments already encoded.");
        }
        else
        {
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            job.BeginStage(L.T("Finding split points"));
            ctx.Changed(job);
            double target = Math.Max(5, cs.TargetSeconds);
            var keyframes = await KeyframesNearAsync(ctx, videoTrack.File, video.Index,
                SplitTimes(start - delay, end - delay, target, workers).ToList(), ct);
            var bounds = BoundsFromKeyframes(keyframes.Select(k => k + delay).ToList(), start, end, target, workers);
            manifest = new Manifest(key, bounds, Enumerable.Repeat(false, bounds.Count - 1).ToList());
            job.AppendLog($"Chunks: {manifest.Done.Count} segments ({(keyframes.Count > 1 ? "keyframe aligned" : "fixed length")}), {workers} worker(s).");
        }
        Save(manifestPath, manifest);

        // ------------------------------------------------------------ encode segments
        int count = manifest.Done.Count;
        double total = end - manifest.Bounds[0];
        double doneSeconds = Enumerable.Range(0, count).Where(i => manifest.Done[i]).Sum(i => manifest.Bounds[i + 1] - manifest.Bounds[i]);
        var running = new ConcurrentDictionary<int, EncodeProgress>();
        var lockObj = new object();
        var lastNotify = DateTime.MinValue;

        void Report()
        {
            if ((DateTime.UtcNow - lastNotify).TotalMilliseconds < 400) return;
            lastNotify = DateTime.UtcNow;
            double pos;
            long size;
            lock (lockObj)
            {
                pos = doneSeconds + running.Values.Sum(p => p.Position.TotalSeconds);
                size = Enumerable.Range(0, count).Where(i => manifest.Done[i]).Sum(i => FileSize(ChunkPath(dir, i))) + running.Values.Sum(p => p.SizeBytes);
            }
            job.Progress = new EncodeProgress(TimeSpan.FromSeconds(pos), TimeSpan.FromSeconds(total),
                running.Values.Sum(p => p.Fps), running.Values.Sum(p => p.Speed), size, 0, false);
            int finished;
            lock (lockObj) finished = manifest.Done.Count(d => d);
            job.Stage = L.T("Encoding segments {0}/{1}", finished, count);
            ctx.Changed(job);
        }

        using var failCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // A steady pool: a segment starts as soon as a worker is free; the first starts are staggered and the segments
        // have varied lengths (shorter towards the end), so the encoders don't all start and stop at the same moment.
        using var gate = new SemaphoreSlim(workers);
        int started = 0;
        string? failure = null;
        var tasks = new List<Task>();
        for (int i = 0; i < count; i++)
        {
            if (manifest.Done[i]) continue;
            int idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(failCts.Token);
                try
                {
                    int order = Interlocked.Increment(ref started) - 1;
                    if (order > 0 && order < workers && StaggerSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(order * StaggerSeconds), failCts.Token);
                    var target = ChunkPath(dir, idx);
                    var tmp = target + ".part.mkv";
                    var compiled = CommandBuilder.Compile(baseCtx with
                    {
                        Chunk = new ChunkRequest(manifest.Bounds[idx], manifest.Bounds[idx + 1]),
                        ForcedOutputPath = tmp,
                    });
                    if (compiled.HasErrors) throw new InvalidOperationException(compiled.Issues.First(x => x.IsError).Message);
                    if (idx == manifest.Done.IndexOf(false)) job.AppendLog("Segment command: " + compiled.Plan.CommandLine(ctx.Tools.Ffmpeg));
                    var parser = new ProgressParser(compiled.OutputDuration);
                    var errors = new System.Text.StringBuilder();
                    int exit = await JobRunner.RunStreamingAsync(ctx.Tools.Ffmpeg, compiled.Plan.Arguments, workDir, ctx.Processes, ctx.Settings.Priority,
                        compiled.Pipe, ctx.Tools.VsPipe,
                        line => { if (parser.Feed(line) is { } pr) { running[idx] = pr; Report(); } },
                        line => { lock (errors) { if (errors.Length < 20000) errors.AppendLine(line); } }, failCts.Token);
                    running.TryRemove(idx, out _);
                    if (exit != 0)
                    {
                        TryDelete(tmp);
                        job.AppendLog($"Segment {idx + 1} failed:\n{errors}");
                        throw new InvalidOperationException(L.T("Segment {0} failed: {1}", idx + 1, JobRunner.LastError(errors.ToString()) ?? $"exit {exit}"));
                    }
                    File.Move(tmp, target, overwrite: true);
                    lock (lockObj)
                    {
                        manifest.Done[idx] = true;
                        doneSeconds += manifest.Bounds[idx + 1] - manifest.Bounds[idx];
                        Save(manifestPath, manifest);
                    }
                    Report();
                }
                catch (OperationCanceledException)
                {
                    TryDelete(ChunkPath(dir, idx) + ".part.mkv");
                    throw;
                }
                catch (Exception ex)
                {
                    TryDelete(ChunkPath(dir, idx) + ".part.mkv");
                    failure ??= ex.Message;
                    await failCts.CancelAsync();
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }
        try { await Task.WhenAll(tasks); }
        catch (Exception) when (failure is not null && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(failure);
        }
        ct.ThrowIfCancellationRequested();

        // ------------------------------------------------------------ assemble
        int code = await AssembleAsync(job, ctx, baseCtx, final, dir, Enumerable.Range(0, count).Select(i => ChunkPath(dir, i)).ToList(),
            manifest.Bounds[0], workDir, ct);
        if (code == 0)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        return code;
    }

    /// <summary>
    /// Joins encoded video segments (concat, no re-encoding) with the audio, subtitles, attachments and chapters,
    /// made in a separate pass on the final timeline. <paramref name="firstBound"/> = start of the first segment.
    /// </summary>
    /// <summary>
    /// Step A of the assembly: audio, subtitles, attachments, chapters on the final timeline, trimmed last, in
    /// dir/rest.mkv. Kept with a marker so a resumed encode doesn't redo it. Returns (exit code, has tracks).
    /// </summary>
    internal static async Task<(int Exit, bool HasRest)> BuildRestAsync(QueueJob job, RunnerContext ctx, CompileContext baseCtx, string dir,
        string workDir, CancellationToken ct)
    {
        var rest = Path.Combine(dir, "rest.mkv");
        var marker = rest + ".done";
        var restPlan = CommandBuilder.Compile(baseCtx with { RestOnly = true, ForcedOutputPath = rest });
        bool hasRest = restPlan.Plan.Arguments.Contains("-map");
        if (!hasRest) return (0, false);
        if (File.Exists(marker) && File.Exists(rest)) return (0, true);
        TryDelete(rest);
        TryDelete(marker);
        job.AppendLog("Assemble (audio/subtitles): " + restPlan.Plan.CommandLine(ctx.Tools.Ffmpeg));
        int rc = await JobRunner.RunStreamingAsync(ctx.Tools.Ffmpeg, restPlan.Plan.Arguments, workDir, ctx.Processes, ctx.Settings.Priority,
            null, line => { lock (job) job.AppendLog(line); }, ct);
        if (rc == 0) File.WriteAllText(marker, "");
        return (rc, true);
    }

    internal static async Task<int> AssembleAsync(QueueJob job, RunnerContext ctx, CompileContext baseCtx, CompiledJob final, string dir,
        IReadOnlyList<string> segments, double firstBound, string workDir, CancellationToken ct, Task<(int Exit, bool HasRest)>? restTask = null)
    {
        var s = job.Settings;
        job.BeginStage(L.T("Joining segments"));
        ctx.Changed(job);
        var list = Path.Combine(dir, "concat.txt");
        File.WriteAllLines(list, segments.Select(f => "file '" + f.Replace("'", "'\\''") + "'"));
        var rest = Path.Combine(dir, "rest.mkv");
        var (rc, hasRest) = await (restTask ?? BuildRestAsync(job, ctx, baseCtx, dir, workDir, ct));
        if (rc != 0) return rc;

        // Step B: concatenated video + step A, pure stream copy without any seek (no lost GOP, no shifted audio).
        double cut = job.Edit.TrimStart is > 0 and var ts ? ts : 0;
        var container = final.Container;
        // -copyts: FFmpeg would otherwise move each input to start at 0, i.e. lose a delay of the intermediate tracks
        // (an audio track starting at 2 s would start with the video).
        var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-copyts", "-f", "concat", "-safe", "0" };
        if (Math.Abs(firstBound - cut) >= 0.0005) args.AddRange(["-itsoffset", CommandBuilder.Sec(firstBound - cut)]);
        args.AddRange(["-i", list]);
        if (hasRest)
        {
            // Tracks moved earlier than the video were shifted to 0 in the intermediate file (Matroska has no negative
            // times): move them back, the final muxer then shifts everything together like a single-pass encode.
            double earliest = final.Tracks.Where(t => t.Include && t.Kind is "audio" or "subtitle").Select(t => t.DelaySeconds).DefaultIfEmpty(0).Min();
            if (earliest < -0.0005) args.AddRange(["-itsoffset", CommandBuilder.Sec(earliest)]);
            args.AddRange(["-i", rest]);
        }
        args.AddRange(["-map", "0:v:0", "-c:v", "copy"]);
        if (s.EffectiveCodec == VideoCodec.HEVC && container is "mp4" or "mov") args.AddRange(["-tag:v", "hvc1"]);
        if (hasRest)
        {
            args.AddRange(["-map", "1:a?", "-c:a", "copy"]);
            args.AddRange(["-map", "1:s?", "-c:s", container switch { "mp4" or "mov" => "mov_text", "webm" => "webvtt", _ => "copy" }]);
            if (container == "mkv") args.AddRange(["-map", "1:t?", "-c:t", "copy"]);
            args.AddRange(["-map_metadata", "1", "-map_chapters", "1"]);
        }
        args.AddRange(final.Hdr.ColorArgs);
        args.AddRange(container switch
        {
            "mp4" or "mov" => ["-movflags", "+faststart"],
            "avi" => ["-f", "avi"],
            "ts" => ["-f", "mpegts"],
            "webm" => ["-f", "webm"],
            _ => [],
        });
        args.AddRange(CommandBuilder.ProgressArgs);
        args.Add(final.Plan.OutputPath);
        job.AppendLog("Assemble (join): " + new EncodePlan(args, "").CommandLine(ctx.Tools.Ffmpeg));
        var ap = new ProgressParser(final.OutputDuration);
        return await JobRunner.RunStreamingAsync(ctx.Tools.Ffmpeg, args, workDir, ctx.Processes, ctx.Settings.Priority,
            line => { if (ap.Feed(line) is { } pr) { job.Progress = pr; ctx.Changed(job); } },
            line => job.AppendLog(line), ct);
    }


    /// <summary>Split points between start and end: keyframes at least <paramref name="target"/> apart, or fixed steps.</summary>
    public static List<double> SplitPoints(IReadOnlyList<double> keyframes, double start, double end, double target)
    {
        var bounds = new List<double> { start };
        var kf = keyframes.Where(k => k > start + 0.5 && k < end - 0.5).Order().ToList();
        if (kf.Count > 0)
        {
            foreach (var k in kf)
                if (k - bounds[^1] >= target && end - k >= target * 0.3) bounds.Add(k);
        }
        if (bounds.Count == 1 && end - start > target * 1.5)
        {
            // No usable keyframes (intra-only, huge GOP…): fixed segments, still frame-exact thanks to trim=.
            for (double t = start + target; end - t >= target * 0.3; t += target) bounds.Add(t);
        }
        bounds.Add(end);
        return bounds;
    }

    /// <summary>
    /// Wanted split times. One worker: every <paramref name="target"/> seconds. Several (av1an-like steady pool): at
    /// least three segments per worker when the video allows it (never under 10 s), lengths varied around the target,
    /// and shorter segments towards the end so the queue drains gradually instead of all workers ending together.
    /// </summary>
    public static IEnumerable<double> SplitTimes(double from, double to, double target, int workers)
    {
        if (workers <= 1) { foreach (var t in StepTimes(from, to, target)) yield return t; yield break; }
        double dur = to - from;
        target = Math.Max(Math.Min(target, dur / (3.0 * workers)), Math.Min(target, 10));
        double[] pattern = [1.0, 0.7, 1.3, 0.85, 1.15];
        double pos = from;
        for (int i = 0; ; i++)
        {
            double remaining = to - pos;
            double factor = pattern[i % pattern.Length];
            if (remaining < target * workers * 1.5) factor = Math.Max(0.4, factor * 0.6);   // the tail: shorter segments
            double step = target * factor;
            if (remaining - step < step * 0.5) yield break;
            pos += step;
            yield return pos;
        }
    }

    private static IEnumerable<double> StepTimes(double from, double to, double step)
    {
        for (double t = from + step; to - t >= step * 0.3; t += step) yield return t;
    }

    /// <summary>
    /// Split points from keyframes found near each wanted time: [start, keyframes at least half a step apart, end].
    /// Fixed steps when the file gave none (intra-only, unreadable index…): still frame-exact thanks to trim=.
    /// </summary>
    public static List<double> BoundsFromKeyframes(IReadOnlyList<double> keyframes, double start, double end, double target, int workers = 1)
    {
        var bounds = new List<double> { start };
        double min = workers > 1 ? 0.25 : 0.5;   // varied lengths: the shortest wanted segment is about 0.4 × target
        foreach (var k in keyframes.Where(k => k > start + 0.5 && k < end - 0.5).Order())
            if (k - bounds[^1] >= target * min && end - k >= target * 0.2) bounds.Add(k);
        if (bounds.Count == 1) bounds.AddRange(SplitTimes(start, end, target, workers));
        bounds.Add(end);
        return bounds;
    }

    /// <summary>
    /// The keyframe at or before each time, found by seeking (one keyframe decoded per point, a few probes at a time).
    /// Scanning the whole file instead meant reading tens of GB of a UHD remux before the first segment could start.
    /// </summary>
    private static async Task<List<double>> KeyframesNearAsync(RunnerContext ctx, string file, int stream, List<double> times, CancellationToken ct)
    {
        var found = new ConcurrentBag<double>();
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(times.Select(async t =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var args = new List<string> { "-v", "error" };
                args.AddRange(MediaProber.RobustInputArgs(file).Where(a => a is not "-fflags" and not "+genpts+discardcorrupt" and not "-err_detect" and not "ignore_err"));
                args.AddRange(["-select_streams", stream.ToString(CultureInfo.InvariantCulture), "-skip_frame", "nokey",
                    "-read_intervals", CommandBuilder.Sec(Math.Max(0, t)) + "%+#1",
                    "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", file]);
                var r = await ProcessRunner.RunAsync(ctx.Tools.Ffprobe, args, timeout: TimeSpan.FromMinutes(1), ct: ct);
                foreach (var l in r.StdOut.Split('\n'))
                    if (double.TryParse(l.Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) found.Add(v);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            finally { gate.Release(); }
        }));
        return [.. found.Distinct().Order()];
    }

    /// <summary>Everything that changes the encoded video must change the key.</summary>
    internal static string Key(QueueJob job)
    {
        // Disc titles ("…::title=3") are not files: the disc folder/image identifies them.
        var real = KnA.Core.Media.Discs.DiscSource.ParseTitlePath(job.SourcePath) is { } t ? t.Disc.Root : job.SourcePath;
        var fi = new FileInfo(real);
        long length = fi.Exists ? fi.Length : 0;
        var when = fi.Exists ? fi.LastWriteTimeUtc : Directory.Exists(real) ? Directory.GetLastWriteTimeUtc(real) : DateTime.MinValue;
        var data = JsonSerializer.Serialize(new
        {
            job.SourcePath, Length = length, LastWriteTimeUtc = when,
            Settings = job.Settings with { Chunks = job.Settings.Chunks with { Workers = 0 }, PresetName = null },
            Edit = job.Edit with { OutputFolder = null, KeepChapters = true },
        }, SettingsStore.Json);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private static string ChunkPath(string dir, int i) => Path.Combine(dir, $"segment_{i.ToString("D5", CultureInfo.InvariantCulture)}.mkv");

    private static void Save(string path, Manifest m)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m));
        File.Move(tmp, path, overwrite: true);
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    /// <summary>Removes segment folders older than <paramref name="days"/> (abandoned jobs).</summary>
    public static void CleanOld(SettingsStore store, int days = 14)
    {
        try
        {
            var root = ChunksRoot(store);
            if (!Directory.Exists(root)) return;
            foreach (var d in new DirectoryInfo(root).GetDirectories().Where(d => d.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-days)))
                try { d.Delete(true); } catch (IOException) { }
        }
        catch (IOException) { }
    }
}
