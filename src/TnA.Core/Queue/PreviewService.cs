using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TnA.Core.Encoding;
using TnA.Core.Localization;
using TnA.Core.Media;
using TnA.Core.Processing;
using TnA.Core.Settings;

namespace TnA.Core.Queue;

/// <summary>
/// Still frames, thumbnails, audio waveforms and short clips for the editors.
/// "Final" frames go through exactly the same pipeline as the real job (track plan, delays,
/// filters, HDR conversion, burned subtitles with their fonts), so what you see is what you get.
/// Results are cached per job and settings.
/// </summary>
public sealed class PreviewService(QueueService queue)
{
    private readonly SemaphoreSlim _gate = new(3);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();

    private string JobDir(Guid id) => Path.Combine(queue.Store.TempDirectory, "preview", id.ToString("N"));

    public void Forget(Guid id)
    {
        try { if (Directory.Exists(JobDir(id))) Directory.Delete(JobDir(id), true); } catch (IOException) { }
    }

    /// <summary>A frame at <paramref name="seconds"/>: the untouched source, or the final result.</summary>
    public Task<string> FrameAsync(Guid id, double seconds, bool final, int maxWidth = 1280, CancellationToken ct = default)
    {
        var job = queue.Find(id) ?? throw new KeyNotFoundException();
        var key = $"frame-{(final ? "final-" + SettingsHash(job) : "orig")}-{Ms(seconds)}-{maxWidth}.png";
        return Cached(job, key, async (dir, path) =>
        {
            var media = await EnsureMedia(job, ct);
            var settings = final ? job.Settings : job.Settings with { VideoFilters = new(), Resolution = 0, Profile = ProfileKind.UniversalMkv };
            var planned = JobRunner.BuildContext(job, queue.Settings, queue.Tools).Edit;
            var edit = final ? planned : planned with { Tracks = (planned.Tracks ?? TrackPlanner.Default(media, job.Settings)).Select(t => t with { Burn = false }).ToList() };
            PreparedSubtitles? subs = final ? await PrepareBurnAsync(job, dir, ct) : null;
            var ctx = JobRunner.BuildContext(job, queue.Settings, queue.Tools) with
            {
                Settings = settings with { Profile = settings.ProfileDefinition.UsesVideoFilters ? settings.Profile : ProfileKind.UniversalMkv },
                Edit = edit with { TrimStart = null, TrimEnd = null },
                Burn = subs,
                Preview = new PreviewRequest(PreviewKind.Frame, Math.Max(0, seconds), MaxWidth: maxWidth),
                ForcedOutputPath = path,
            };
            await RunCompiled(CommandBuilder.Compile(ctx), dir, ct);
        }, ct);
    }

    /// <summary>Small JPEG of the source at a keyframe near <paramref name="seconds"/> (timeline strip).</summary>
    public Task<string> ThumbnailAsync(Guid id, double seconds, int width = 192, CancellationToken ct = default)
    {
        var job = queue.Find(id) ?? throw new KeyNotFoundException();
        return Cached(job, $"thumb-{Ms(seconds)}-{width}.jpg", async (dir, path) =>
        {
            await EnsureMedia(job, ct);
            if (Frameserver.IsVapourSynth(job.SourcePath))
            {
                // Script sources: one frame through vspipe.
                var v = job.Media!.PrimaryVideo!;
                long f = Math.Clamp((long)(Math.Max(0, seconds) * v.FrameRate), 0, Math.Max(0, v.FrameCount - 1));
                await RunPipe(["-hide_banner", "-nostdin", "-y", "-v", "error", "-f", "yuv4mpegpipe", "-i", "pipe:0", "-frames:v", "1",
                    "-vf", $"scale={width}:-2", "-q:v", "5", "-update", "1", path], new PipeSource(job.SourcePath, f, f), dir, ct);
                return;
            }
            // Fast path: decode keyframes only. After the last keyframe of the file it finds nothing: decode normally then.
            foreach (bool keyOnly in new[] { true, false })
            {
                var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error" };
                if (keyOnly) args.AddRange(["-skip_frame", "nokey"]);
                args.AddRange(MediaProber.RobustInputArgs(job.SourcePath));
                args.AddRange(["-ss", CommandBuilder.Sec(Math.Max(0, seconds)), "-i", job.SourcePath, "-map", "0:v:0", "-frames:v", "1",
                    "-vf", $"scale={width}:-2", "-q:v", "5", "-update", "1", path]);
                try { await Run(args, dir, ct); } catch (InvalidOperationException) when (keyOnly) { }
                if (File.Exists(path) && new FileInfo(path).Length > 0) return;
            }
        }, ct);
    }

    /// <summary>PNG waveform of an audio track (whole duration, delay not applied).</summary>
    public Task<string> WaveformAsync(Guid id, string file, int stream, int width = 1600, int height = 120, CancellationToken ct = default)
    {
        var job = queue.Find(id) ?? throw new KeyNotFoundException();
        var key = $"wave-{Hash(file)}-{stream}-{width}x{height}.png";
        return Cached(job, key, async (dir, path) =>
        {
            if (Frameserver.IsVapourSynth(file))
            {
                // Script audio (output N): render it once, then draw the waveform of the rendered file.
                var ctx = new RunnerContext(queue.Tools!, queue.Settings, queue.Store, new ProcessGroup(), _ => { });
                var rendered = await JobRunner.RenderScriptAudioAsync([new TrackPlan { File = file, Stream = stream, Kind = "audio" }],
                    Path.Combine(dir, "script-audio"), ctx, job, ct);
                file = rendered.Values.First();
                stream = 0;
            }
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error" };
            args.AddRange(MediaProber.RobustInputArgs(file));
            args.AddRange(["-i", file, "-filter_complex",
                $"[0:{stream}]aformat=channel_layouts=mono,showwavespic=s={width}x{height}:colors=#9B7CFF[w]",
                "-map", "[w]", "-frames:v", "1", "-update", "1", path]);
            await Run(args, dir, ct, TimeSpan.FromMinutes(5));
        }, ct);
    }

    /// <summary>A short playable MP4 (H.264/AAC, SDR) of the final result starting at <paramref name="seconds"/>.</summary>
    public Task<string> ClipAsync(Guid id, double seconds, double length = 6, CancellationToken ct = default)
    {
        var job = queue.Find(id) ?? throw new KeyNotFoundException();
        var key = $"clip-{SettingsHash(job)}-{Ms(seconds)}-{Ms(length)}.mp4";
        return Cached(job, key, async (dir, path) =>
        {
            await EnsureMedia(job, ct);
            var subs = await PrepareBurnAsync(job, dir, ct);
            var baseCtx = JobRunner.BuildContext(job, queue.Settings, queue.Tools);
            var ctx = baseCtx with
            {
                Edit = baseCtx.Edit with { TrimStart = null, TrimEnd = null },
                ScriptAudio = await ScriptAudioAsync(job, dir, ct),
                Burn = subs,
                Settings = job.Settings.ProfileDefinition.UsesVideoFilters ? job.Settings : job.Settings with { Profile = ProfileKind.UniversalMkv },
                Preview = new PreviewRequest(PreviewKind.Clip, Math.Max(0, seconds), Math.Clamp(length, 1, 30), 1280),
                ForcedOutputPath = path,
            };
            await RunCompiled(CommandBuilder.Compile(ctx), dir, ct, TimeSpan.FromMinutes(3));
        }, ct);
    }

    /// <summary>Finds black borders by sampling a few points of the video (cropdetect), in displayed orientation.</summary>
    public async Task<CropSettings?> DetectCropAsync(Guid id, CancellationToken ct = default)
    {
        var job = queue.Find(id) ?? throw new KeyNotFoundException();
        var media = await EnsureMedia(job, ct);
        var v = FilterBuilder.Oriented(media.PrimaryVideo!, job.Settings.VideoFilters);
        double dur = Math.Max(1, media.Duration.TotalSeconds);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
        // Union of the boxes: never crop away content that appears in any sample.
        void Union((int W, int H, int X, int Y) b)
        {
            minX = Math.Min(minX, b.X); minY = Math.Min(minY, b.Y);
            maxX = Math.Max(maxX, b.X + b.W); maxY = Math.Max(maxY, b.Y + b.H);
        }
        foreach (var f in new[] { 0.15, 0.35, 0.5, 0.65, 0.85 })
        {
            string? cropLine;
            if (Frameserver.IsVapourSynth(job.SourcePath))
            {
                long start = (long)(dur * f * media.PrimaryVideo!.FrameRate);
                var err = new StringBuilder();
                await JobRunner.RunStreamingAsync(queue.Tools!.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "info", "-f", "yuv4mpegpipe", "-i", "pipe:0",
                        "-frames:v", "12", "-vf", "cropdetect=limit=24:round=2:reset=0", "-f", "null", "-"],
                    null, new ProcessGroup(), ProcessPriority.Normal, new PipeSource(job.SourcePath, start, start + 12), queue.Tools.VsPipe,
                    null, l => { lock (err) err.AppendLine(l); }, ct);
                cropLine = err.ToString().Split('\n').LastOrDefault(l => l.Contains("crop=", StringComparison.Ordinal));
                if (ParseCrop(cropLine) is { } box) Union(box);
                continue;
            }
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "info" };
            args.AddRange(MediaProber.RobustInputArgs(job.SourcePath));
            args.AddRange(FilterBuilder.OrientationInputArgs(media.PrimaryVideo!, job.Settings.VideoFilters));
            args.AddRange(["-ss", CommandBuilder.Sec(dur * f), "-i", job.SourcePath, "-map", $"0:{media.PrimaryVideo!.Index}",
                "-frames:v", "12", "-vf", "cropdetect=limit=24:round=2:reset=0", "-f", "null", "-"]);
            var r = await ProcessRunner.RunAsync(queue.Tools!.Ffmpeg, args, timeout: TimeSpan.FromSeconds(60), ct: ct);
            if (ParseCrop(r.StdErr.Split('\n').LastOrDefault(l => l.Contains("crop=", StringComparison.Ordinal))) is { } b2) Union(b2);
        }
        if (minX == int.MaxValue) return null;
        var crop = new CropSettings(minX, minY, Math.Max(0, v.Width - maxX), Math.Max(0, v.Height - maxY));
        return crop.IsEmpty ? null : crop;
    }

    private static (int W, int H, int X, int Y)? ParseCrop(string? line)
    {
        if (line is null) return null;
        var c = line[(line.LastIndexOf("crop=", StringComparison.Ordinal) + 5)..].Trim().Split(':');
        if (c.Length < 4 || !int.TryParse(c[0], out var w) || !int.TryParse(c[1], out var h)
            || !int.TryParse(c[2], out var x) || !int.TryParse(c[3].Split(' ')[0], out var y) || w <= 0 || h <= 0) return null;
        return (w, h, x, y);
    }

    private async Task RunPipe(List<string> args, PipeSource pipe, string dir, CancellationToken ct)
    {
        var tools = queue.Tools ?? throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        await _gate.WaitAsync(ct);
        try
        {
            var err = new StringBuilder();
            int exit = await JobRunner.RunStreamingAsync(tools.Ffmpeg, args, dir, new ProcessGroup(), ProcessPriority.Normal, pipe, tools.VsPipe,
                null, l => { lock (err) err.AppendLine(l); }, ct);
            if (exit != 0) throw new InvalidOperationException(JobRunner.LastError(err.ToString()) ?? err.ToString().Trim());
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- internals

    private async Task<MediaInfo> EnsureMedia(QueueJob job, CancellationToken ct)
    {
        var tools = queue.Tools ?? throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        var prober = MediaProber.For(tools);
        job.Media ??= await prober.ProbeAsync(job.SourcePath, ct);
        JobRunner.EnsureFrameserverScript(job, queue.Store);
        await JobRunner.ProbeExternalsAsync(job, prober, ct);
        return job.Media;
    }

    private async Task<PreparedSubtitles?> PrepareBurnAsync(QueueJob job, string dir, CancellationToken ct)
    {
        var media = await EnsureMedia(job, ct);
        var tracks = JobRunner.BuildContext(job, queue.Settings, queue.Tools).Edit.Tracks ?? TrackPlanner.Default(media, job.Settings);
        if (!job.Settings.ProfileDefinition.UsesVideoFilters || tracks.FirstOrDefault(t => t.Burn && t.Kind == "subtitle") is not { } burn) return null;
        var subDir = Path.Combine(dir, "subs-" + Hash($"{burn.File}|{burn.Stream}|{burn.DelayMs}|{string.Join(',', job.Edit.ExternalFiles)}"));
        Directory.CreateDirectory(subDir);
        var marker = Path.Combine(subDir, "ready.json");
        if (File.Exists(marker))
            return JsonSerializer.Deserialize<PreparedSubtitles>(File.ReadAllText(marker), SettingsStore.Json);
        var ctx = new RunnerContext(queue.Tools!, queue.Settings, queue.Store, new ProcessGroup(), _ => { });
        var prepared = await JobRunner.PrepareSubtitlesAsync(job, burn, tracks, subDir, ctx, ct);
        if (prepared is null) return null;
        // Commands run in the preview folder: relative names must point inside subDir.
        var rel = Path.GetFileName(subDir);
        var result = prepared with
        {
            RelativeSubtitleFile = prepared.RelativeSubtitleFile is { } f ? rel + "/" + f : null,
            RelativeFontsDir = prepared.RelativeFontsDir is { } d ? rel + "/" + d : null,
        };
        File.WriteAllText(marker, JsonSerializer.Serialize(result, SettingsStore.Json));
        return result;
    }

    private async Task RunCompiled(CompiledJob compiled, string dir, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (compiled.HasErrors) throw new InvalidOperationException(compiled.Issues.First(i => i.IsError).Message);
        if (compiled.Pipe is null)
        {
            await Run(compiled.Plan.Arguments, dir, ct, timeout);
            return;
        }
        // VapourSynth video: vspipe (frame range around the requested time) → ffmpeg stdin.
        var tools = queue.Tools!;
        await _gate.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(120));
            var err = new StringBuilder();
            int exit = await JobRunner.RunStreamingAsync(tools.Ffmpeg, compiled.Plan.Arguments, dir, new ProcessGroup(), ProcessPriority.Normal,
                compiled.Pipe, tools.VsPipe, null, l => { lock (err) err.AppendLine(l); }, cts.Token);
            if (exit != 0) throw new InvalidOperationException(JobRunner.LastError(err.ToString()) ?? err.ToString().Trim());
        }
        finally { _gate.Release(); }
    }

    /// <summary>Script audio for clip previews, rendered once per job.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ScriptAudioAsync(QueueJob job, string dir, CancellationToken ct)
    {
        var tracks = JobRunner.BuildContext(job, queue.Settings, queue.Tools).Edit.Tracks
                     ?? TrackPlanner.Default(job.Media!, job.Settings, frameserver: JobRunner.FrameserverMedia(job));
        if (!tracks.Any(t => t.Include && t.Kind == "audio" && Frameserver.IsVapourSynth(t.File))) return new Dictionary<string, string>();
        var ctx = new RunnerContext(queue.Tools!, queue.Settings, queue.Store, new ProcessGroup(), _ => { });
        return await JobRunner.RenderScriptAudioAsync(tracks, Path.Combine(dir, "script-audio"), ctx, job, ct);
    }

    private async Task Run(IEnumerable<string> args, string dir, CancellationToken ct, TimeSpan? timeout = null)
    {
        var tools = queue.Tools ?? throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        await _gate.WaitAsync(ct);
        try
        {
            var list = args.ToList();
            try { File.WriteAllText(Path.Combine(dir, "last-command.txt"), new EncodePlan(list, "").CommandLine(tools.Ffmpeg)); } catch (IOException) { }
            var r = await ProcessRunner.RunAsync(tools.Ffmpeg, list, workingDirectory: dir, timeout: timeout ?? TimeSpan.FromSeconds(90), ct: ct);
            if (r.ExitCode != 0) throw new InvalidOperationException(JobRunner.LastError(r.StdErr) ?? r.StdErr.Trim());
        }
        finally { _gate.Release(); }
    }

    private async Task<string> Cached(QueueJob job, string key, Func<string, string, Task> produce, CancellationToken ct)
    {
        var dir = JobDir(job.Id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, key);
        var gate = _keyLocks.GetOrAdd(path, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            await produce(dir, path);
            if (!File.Exists(path)) throw new InvalidOperationException(L.T("The preview could not be generated."));
            return path;
        }
        finally { gate.Release(); }
    }

    private static string SettingsHash(QueueJob job) =>
        Hash(JsonSerializer.Serialize(job.Settings, SettingsStore.Json) + JsonSerializer.Serialize(job.Edit with { TrimStart = null, TrimEnd = null }, SettingsStore.Json));

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();

    private static string Ms(double seconds) => ((long)Math.Round(seconds * 1000)).ToString(CultureInfo.InvariantCulture);
}
