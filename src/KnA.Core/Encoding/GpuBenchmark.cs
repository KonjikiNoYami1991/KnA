using System.Diagnostics;
using System.Globalization;
using KnA.Core.Localization;
using KnA.Core.Processing;
using KnA.Core.Queue;

namespace KnA.Core.Encoding;

/// <summary>Speed of one rigaya profile on this GPU: frames per second on a 1080p test clip.</summary>
public sealed record GpuBenchmarkResult(EncoderBackend Backend, VideoCodec Codec, string Profile, double Fps1080, DateTimeOffset At);

/// <summary>
/// Optional one-time mini benchmark: a 10-second 1080p test clip encoded by the rigaya encoder with each speed /
/// quality profile (GPU decoding included, as in a real encode). The speeds then estimate each job's time and
/// suggest the profile (Video → Quality and speed). Results are kept in the settings until the next run.
/// </summary>
public static class GpuBenchmark
{
    private const int Seconds = 10, Rate = 24;

    public static async Task<List<GpuBenchmarkResult>> RunAsync(QueueService q, EncoderBackend backend, VideoCodec codec,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var tools = q.Tools ?? throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        var exe = JobRunner.RigayaFor(tools).GetValueOrDefault(backend)
                  ?? throw new InvalidOperationException(L.T("{0} is not installed: add it in Settings → Components, or choose FFmpeg as the encoder program.", RigayaEncoders.DisplayName(backend)));
        var dir = Path.Combine(q.Store.TempDirectory, "gpu-bench-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var clip = Path.Combine(dir, "clip.mkv");
            progress?.Report(L.T("Preparing the test clip…"));
            var g = await ProcessRunner.RunAsync(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi",
                "-i", $"testsrc2=size=1920x1080:rate={Rate}:duration={Seconds}", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", clip], ct: ct);
            if (g.ExitCode != 0) throw new InvalidOperationException(g.StdErr.Trim());
            var results = new List<GpuBenchmarkResult>();
            foreach (var p in RigayaProfiles.For(backend, codec))
            {
                progress?.Report(L.T("Measuring \"{0}\"…", p.Name));
                var o = RigayaProfiles.Apply(new EncoderOverrides { Program = EncoderProgram.Rigaya }, p);
                var enc = RigayaEncoders.EncoderArgs(backend, codec, QualityLevel.Normal, false, o, Rate, new HdrPlan(),
                    new Media.VideoStream { Index = 0, Kind = "video", CodecName = "h264", Width = 1920, Height = 1080, FrameRate = Rate }, false);
                var output = Path.Combine(dir, p.Id + ".mkv");
                var sw = Stopwatch.StartNew();
                var r = await ProcessRunner.RunAsync(exe, ["--avhw", "-i", clip, .. enc, "-o", output], workingDirectory: dir, timeout: TimeSpan.FromMinutes(5), ct: ct);
                sw.Stop();
                if (r.ExitCode != 0 || !File.Exists(output))
                    throw new InvalidOperationException(L.T("{0} failed with \"{1}\": {2}", RigayaEncoders.DisplayName(backend), p.Name, (r.StdErr + r.StdOut).Trim().Split('\n').LastOrDefault() ?? ""));
                results.Add(new GpuBenchmarkResult(backend, codec, p.Id, Seconds * Rate / Math.Max(0.05, sw.Elapsed.TotalSeconds), DateTimeOffset.Now));
            }
            q.Settings.GpuBenchmarks = [.. q.Settings.GpuBenchmarks.Where(x => x.Backend != backend || x.Codec != codec), .. results];
            q.SaveSettings();
            return results;
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>
    /// Expected encode time of a job with each measured profile: the 1080p speed scaled by the output's pixel count
    /// (the GPU's time grows with the pixels), for the frames of the job. Empty without a benchmark.
    /// </summary>
    public static List<(RigayaProfile Profile, TimeSpan Time, double Fps)> Estimate(IEnumerable<GpuBenchmarkResult> bench, EncoderBackend backend,
        VideoCodec codec, int width, int height, double frameRate, TimeSpan duration)
    {
        var list = new List<(RigayaProfile, TimeSpan, double)>();
        if (width <= 0 || height <= 0 || duration <= TimeSpan.Zero) return list;
        double pixels = width * (double)height / (1920.0 * 1080.0);
        double frames = duration.TotalSeconds * (frameRate > 0 ? frameRate : 25);
        foreach (var p in RigayaProfiles.For(backend, codec))
            if (bench.LastOrDefault(b => b.Backend == backend && b.Codec == codec && b.Profile == p.Id) is { Fps1080: > 0 } b)
            {
                double fps = b.Fps1080 / Math.Max(0.1, pixels);
                list.Add((p, TimeSpan.FromSeconds(frames / fps), fps));
            }
        return list;
    }

    /// <summary>
    /// The line shown under the profile for a job: expected time with each measured profile and the suggestion, or
    /// null when this encoder/codec was never measured.
    /// </summary>
    public static string? EstimateFor(JobSettings s, Media.MediaInfo source, JobEdit edit, IEnumerable<GpuBenchmarkResult> bench)
    {
        if (source.PrimaryVideo is not { } v) return null;
        var cropped = FilterBuilder.AfterCrop(FilterBuilder.Oriented(v, s.VideoFilters), s.VideoFilters.Crop);
        var scale = ScaleCalculator.Compute(cropped, s.ProfileDefinition.UsesResolution ? s.Resolution : 0);
        var parts = edit.KeptParts(source.Duration.TotalSeconds);
        var duration = parts.Count > 0 ? TimeSpan.FromSeconds(parts.Sum(p => p.End - p.Start)) : source.Duration;
        double fr = s.VideoFilters.Fps ?? v.FrameRate;
        var e = Estimate(bench, s.Backend, s.EffectiveCodec, scale?.Width ?? cropped.Width, scale?.Height ?? cropped.Height, fr, duration);
        if (e.Count == 0) return null;
        return L.T("Expected time: {0}. Suggested: {1}.", Describe(e), Suggest(e, fr)?.Name ?? "");
    }

    /// <summary>The profile to suggest: the most careful one still at least twice real time, else the fastest.</summary>
    public static RigayaProfile? Suggest(IReadOnlyList<(RigayaProfile Profile, TimeSpan Time, double Fps)> estimates, double frameRate)
    {
        if (estimates.Count == 0) return null;
        return estimates.FirstOrDefault(e => e.Fps >= 2 * (frameRate > 0 ? frameRate : 25)).Profile ?? estimates[^1].Profile;
    }

    /// <summary>"Best quality 12 min · Balanced 7 min · Fast 4 min".</summary>
    public static string Describe(IReadOnlyList<(RigayaProfile Profile, TimeSpan Time, double Fps)> estimates) =>
        string.Join(" · ", estimates.Select(e => $"{e.Profile.Name} {Short(e.Time)} ({e.Fps.ToString("0", CultureInfo.InvariantCulture)} fps)"));

    private static string Short(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{Math.Ceiling(t.TotalMinutes):0} min" : $"{Math.Ceiling(t.TotalSeconds):0} s";
}
