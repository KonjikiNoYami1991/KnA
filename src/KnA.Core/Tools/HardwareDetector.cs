using KnA.Core.Encoding;
using KnA.Core.Processing;

namespace KnA.Core.Tools;

public sealed record EncoderCapability(VideoCodec Codec, EncoderBackend Backend, string Encoder,
    bool Compiled, bool Works8Bit, bool Works10Bit, string? Error)
{
    /// <summary>FFmpeg build that provides this encoder; null = the main one.</summary>
    public string? Ffmpeg { get; init; }
    public bool Usable => Works8Bit || Works10Bit;
}

/// <summary>One FFmpeg executable and what it was compiled with.</summary>
public sealed record FfmpegBuild(string Path, string Version, bool IsCustom)
{
    public IReadOnlyList<string> Encoders { get; init; } = [];
    public bool HasLibass { get; init; }
    public bool HasLibplacebo { get; init; }
    public bool HasZscale { get; init; } = true;
    /// <summary>bluray: protocol (libbluray): BDMV folders and Blu-ray ISO images.</summary>
    public bool HasBluray { get; init; }
    /// <summary>dvdvideo demuxer (FFmpeg 7+, libdvdnav): VIDEO_TS folders and DVD ISO images.</summary>
    public bool HasDvdVideo { get; init; }

    public bool Has(string encoder) => Encoders.Contains(encoder);
}

public sealed record FfmpegCapabilities(string Version, IReadOnlyList<EncoderCapability> Encoders,
    bool HasLibass, DateTimeOffset DetectedAt)
{
    /// <summary>Format of this record; older detections are redone automatically.</summary>
    public const int CurrentSchema = 3;
    public int Schema { get; init; }
    public bool HasZscale { get; init; } = true;
    public bool HasLibplacebo { get; init; }
    public bool HasLibwebp { get; init; } = true;
    /// <summary>Main build first, then the custom ones.</summary>
    public IReadOnlyList<FfmpegBuild> Builds { get; init; } = [];

    public bool IsCurrent => Schema >= CurrentSchema;
    public FfmpegBuild? Main => Builds.FirstOrDefault(b => !b.IsCustom) ?? Builds.FirstOrDefault();

    /// <summary>The encoder that will be used: the first usable candidate (custom builds first), else the first one tested.</summary>
    public EncoderCapability? Find(VideoCodec c, EncoderBackend b) =>
        Encoders.FirstOrDefault(e => e.Codec == c && e.Backend == b && e.Usable)
        ?? Encoders.FirstOrDefault(e => e.Codec == c && e.Backend == b);

    public EncoderCapability? Resolve(VideoCodec c, EncoderBackend b, bool tenBit) =>
        Encoders.FirstOrDefault(e => e.Codec == c && e.Backend == b && (tenBit ? e.Works10Bit : e.Works8Bit));

    public bool IsUsable(VideoCodec c, EncoderBackend b, bool tenBit) => Resolve(c, b, tenBit) is not null;

    /// <summary>The build an encoder belongs to (main build when unknown).</summary>
    public FfmpegBuild? BuildOf(EncoderCapability? cap) =>
        cap?.Ffmpeg is { } path ? Builds.FirstOrDefault(b => b.Path == path) ?? Main : Main;

    public bool AnyBuildHas(string encoder) => Builds.Any(b => b.Has(encoder));
}

/// <summary>
/// Doesn't trust "ffmpeg -encoders": a hardware encoder is compiled into most builds even when there is
/// no GPU/driver. Each candidate is verified with a tiny real encode (8-bit and, where offered, 10-bit)
/// using exactly the arguments KnA will use later. Custom builds (Settings → custom FFmpeg) are tested too,
/// and come first: an encoder that works there is taken from there.
/// </summary>
public static class HardwareDetector
{
    public static Task<FfmpegCapabilities> DetectAsync(string ffmpeg, string? vaapiDevice,
        IProgress<string>? log = null, CancellationToken ct = default) =>
        DetectAsync(ffmpeg, [], vaapiDevice, log, ct);

    public static async Task<FfmpegCapabilities> DetectAsync(string ffmpeg, IReadOnlyList<string> customBuilds, string? vaapiDevice,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var builds = new List<FfmpegBuild>();
        foreach (var custom in customBuilds.Where(File.Exists).Distinct())
        {
            if (custom == ffmpeg) continue;
            try { builds.Add(await DescribeAsync(custom, true, log, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log?.Report($"{custom}: {ex.Message}"); }
        }
        var main = await DescribeAsync(ffmpeg, false, log, ct);
        builds.Insert(0, main);
        // Testing order: custom builds first (the user added them for a reason), then the main one.
        var testOrder = builds.Where(b => b.IsCustom).Append(main).ToList();

        var results = new List<EncoderCapability>();
        foreach (var codec in Enum.GetValues<VideoCodec>())
            foreach (var backend in VideoEncoders.AllBackends)
                foreach (var name in VideoEncoders.Candidates(codec, backend))
                {
                    var having = testOrder.Where(b => b.Has(name)).ToList();
                    if (having.Count == 0)
                    {
                        results.Add(new(codec, backend, name, false, false, false, "Not compiled into this FFmpeg build."));
                        continue;
                    }
                    if (backend == EncoderBackend.Vaapi && !File.Exists(vaapiDevice ?? VideoEncoders.DefaultVaapiDevice))
                    {
                        results.Add(new(codec, backend, name, true, false, false, "No VA-API render device."));
                        continue;
                    }
                    foreach (var build in having)
                    {
                        log?.Report($"Testing {name}{(build.IsCustom ? $" ({Path.GetFileName(Path.GetDirectoryName(build.Path))})" : "")}…");
                        bool offers10 = VideoEncoders.Supports10Bit(codec, backend);
                        bool only10 = VideoEncoders.Always10Bit(codec);
                        var (ok8, err8) = only10 ? (false, null) : await TestAsync(build.Path, codec, backend, name, false, vaapiDevice, ct);
                        bool ok10 = false;
                        string? err10 = null;
                        if (offers10 && !codec.IsProfessional()) (ok10, err10) = await TestAsync(build.Path, codec, backend, name, true, vaapiDevice, ct);
                        else if (codec.IsProfessional()) ok10 = ok8; // professional codecs pick their own depth
                        log?.Report($"  {name}: 8-bit {(ok8 ? "OK" : "no")}" + (offers10 && !codec.IsProfessional() ? $", 10-bit {(ok10 ? "OK" : "no")}" : ""));
                        results.Add(new(codec, backend, name, true, ok8, ok10, ok8 || ok10 ? null : err8 ?? err10)
                        {
                            Ffmpeg = build.IsCustom ? build.Path : null,
                        });
                    }
                }

        return new FfmpegCapabilities(main.Version, results, main.HasLibass, DateTimeOffset.Now)
        {
            Schema = FfmpegCapabilities.CurrentSchema,
            HasZscale = main.HasZscale,
            HasLibplacebo = main.HasLibplacebo,
            HasLibwebp = main.Has("libwebp"),
            Builds = builds,
        };
    }

    /// <summary>Version, encoders, filters, demuxers and protocols of one FFmpeg executable.</summary>
    public static async Task<FfmpegBuild> DescribeAsync(string ffmpeg, bool custom, IProgress<string>? log, CancellationToken ct)
    {
        var version = (await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-version"], ct: ct)).StdOut
            .Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
        var encodersOut = (await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], ct: ct)).StdOut;
        var filtersOut = (await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-filters"], ct: ct)).StdOut;
        var demuxOut = (await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-demuxers"], ct: ct)).StdOut;
        var protoOut = (await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-protocols"], ct: ct)).StdOut;
        var encoders = ParseEncoders(encodersOut);
        bool placebo = false;
        if (filtersOut.Contains(" libplacebo ", StringComparison.Ordinal))
        {
            // Compiled in is not enough: it needs a working Vulkan device.
            log?.Report("Testing libplacebo (Vulkan)…");
            var r = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:duration=0.2",
                "-vf", "libplacebo=format=yuv420p", "-frames:v", "2", "-f", "null", "-"], timeout: TimeSpan.FromSeconds(30), ct: ct);
            placebo = r.ExitCode == 0;
        }
        return new FfmpegBuild(ffmpeg, version, custom)
        {
            Encoders = encoders,
            HasLibass = filtersOut.Contains(" subtitles ", StringComparison.Ordinal),
            HasLibplacebo = placebo,
            HasZscale = filtersOut.Contains(" zscale ", StringComparison.Ordinal),
            HasDvdVideo = demuxOut.Split('\n').Any(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, "dvdvideo", ..]),
            HasBluray = protoOut.Split('\n').Any(l => l.Trim() == "bluray"),
        };
    }

    /// <summary>Names from "ffmpeg -encoders" (lines like " V....D libx264  …").</summary>
    public static List<string> ParseEncoders(string encodersOut) =>
        encodersOut.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2 && p[0].Length == 6 && p[0][0] is 'V' or 'A' or 'S')
            .Select(p => p[1]).Distinct().ToList();

    private static async Task<(bool, string?)> TestAsync(string ffmpeg, VideoCodec codec, EncoderBackend backend, string encoder,
        bool tenBit, string? vaapiDevice, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin" };
        args.AddRange(VideoEncoders.GlobalArgs(backend, vaapiDevice));
        args.AddRange(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=0.5"]);
        if (VideoEncoders.FilterTail(backend, codec, tenBit) is { } tail) args.AddRange(["-vf", tail]);
        args.AddRange(VideoEncoders.BuildArgs(new VideoEncodeRequest(codec, backend, QualityLevel.Normal, tenBit,
            codec.IsProfessional() ? ProfileKind.Professional : ProfileKind.UniversalMkv, 25, false) { Encoder = encoder }));
        args.AddRange(["-frames:v", "10", "-f", "null", "-"]);
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, args, timeout: TimeSpan.FromSeconds(45), ct: ct);
            var err = r.StdErr.Trim();
            return (r.ExitCode == 0, r.ExitCode == 0 ? null : err.Split('\n').LastOrDefault(l => l.Length > 0) ?? $"exit {r.ExitCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
