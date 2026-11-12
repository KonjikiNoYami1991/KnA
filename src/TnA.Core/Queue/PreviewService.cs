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
            var edit = final ? job.Edit : job.Edit with { Tracks = (job.Edit.Tracks ?? TrackPlanner.Default(media, job.Settings)).Select(t => t with { Burn = false }).ToList() };
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
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-v", "error", "-skip_frame", "nokey" };
            args.AddRange(MediaProber.RobustInputArgs(job.SourcePath));
            args.AddRange(["-ss", CommandBuilder.Sec(Math.Max(0, seconds)), "-i", job.SourcePath, "-map", "0:v:0", "-frames:v", "1",
                "-vf", $"scale={width}:-2", "-q:v", "5", "-update", "1", path]);
            await Run(args, dir, ct);
        }, ct);
    }

    /// <summary>PNG waveform of an audio track (whole duration, delay not applied).</summary>
    public Task<string> WaveformAsync(Guid id, string file, int stream, int width = 1600, int height = 120, CancellationToken ct = default)
    {
        var job = queue.Find(id) ?? throw new KeyNotFoundException();
        var key = $"wave-{Hash(file)}-{stream}-{width}x{height}.png";
        return Cached(job, key, async (dir, path) =>
        {
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
            var ctx = JobRunner.BuildContext(job, queue.Settings, queue.Tools) with
            {
                Edit = job.Edit with { TrimStart = null, TrimEnd = null },
                Burn = subs,
                Settings = job.Settings.ProfileDefinition.UsesVideoFilters ? job.Settings : job.Settings with { Profile = ProfileKind.UniversalMkv },
                Preview = new PreviewRequest(PreviewKind.Clip, Math.Max(0, seconds), Math.Clamp(length, 1, 30), 1280),
                ForcedOutputPath = path,
            };
            await RunCompiled(CommandBuilder.Compile(ctx), dir, ct, TimeSpan.FromMinutes(3));
        }, ct);
    }

    // ---------------------------------------------------------------- internals

    private async Task<MediaInfo> EnsureMedia(QueueJob job, CancellationToken ct)
    {
        var tools = queue.Tools ?? throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        var prober = new MediaProber(tools.Ffprobe);
        job.Media ??= await prober.ProbeAsync(job.SourcePath, ct);
        await JobRunner.ProbeExternalsAsync(job, prober, ct);
        return job.Media;
    }

    private async Task<PreparedSubtitles?> PrepareBurnAsync(QueueJob job, string dir, CancellationToken ct)
    {
        var media = await EnsureMedia(job, ct);
        var tracks = job.Edit.Tracks ?? TrackPlanner.Default(media, job.Settings);
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
        await Run(compiled.Plan.Arguments, dir, ct, timeout);
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
