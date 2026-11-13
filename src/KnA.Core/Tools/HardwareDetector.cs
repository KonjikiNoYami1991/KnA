using KnA.Core.Encoding;
using KnA.Core.Processing;

namespace KnA.Core.Tools;

public sealed record EncoderCapability(VideoCodec Codec, EncoderBackend Backend, string Encoder,
    bool Compiled, bool Works8Bit, bool Works10Bit, string? Error)
{
    public bool Usable => Works8Bit || Works10Bit;
}

public sealed record FfmpegCapabilities(string Version, IReadOnlyList<EncoderCapability> Encoders,
    bool HasLibass, DateTimeOffset DetectedAt)
{
    public bool HasZscale { get; init; } = true;
    public bool HasLibplacebo { get; init; }
    public bool HasLibwebp { get; init; } = true;

    public EncoderCapability? Find(VideoCodec c, EncoderBackend b) =>
        Encoders.FirstOrDefault(e => e.Codec == c && e.Backend == b);

    public bool IsUsable(VideoCodec c, EncoderBackend b, bool tenBit) =>
        Find(c, b) is { } e && (tenBit ? e.Works10Bit : e.Works8Bit);
}

/// <summary>
/// Doesn't trust "ffmpeg -encoders": a hardware encoder is compiled into most builds even when there is
/// no GPU/driver. Each candidate is verified with a tiny real encode (8-bit and, where offered, 10-bit)
/// using exactly the arguments KnA will use later.
/// </summary>
public static class HardwareDetector
{
    public static async Task<FfmpegCapabilities> DetectAsync(string ffmpeg, string? vaapiDevice,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var version = (await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-version"], ct: ct)).StdOut
            .Split('\n').FirstOrDefault()?.Trim() ?? "unknown";
        var encodersOut = (await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], ct: ct)).StdOut;
        var filtersOut = (await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-filters"], ct: ct)).StdOut;
        var compiled = encodersOut.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2 && p[0].Length == 6)
            .Select(p => p[1]).ToHashSet(StringComparer.Ordinal);
        bool libass = filtersOut.Contains(" subtitles ", StringComparison.Ordinal);
        bool zscale = filtersOut.Contains(" zscale ", StringComparison.Ordinal);
        bool placebo = false;
        if (filtersOut.Contains(" libplacebo ", StringComparison.Ordinal))
        {
            // Compiled in is not enough: it needs a working Vulkan device.
            log?.Report("Testing libplacebo (Vulkan)…");
            var r = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:duration=0.2",
                "-vf", "libplacebo=format=yuv420p", "-frames:v", "2", "-f", "null", "-"], timeout: TimeSpan.FromSeconds(30), ct: ct);
            placebo = r.ExitCode == 0;
        }

        var results = new List<EncoderCapability>();
        foreach (var codec in Enum.GetValues<VideoCodec>())
            foreach (var backend in VideoEncoders.AllBackends)
            {
                if (VideoEncoders.EncoderName(codec, backend) is not { } name) continue;
                if (!compiled.Contains(name))
                {
                    results.Add(new(codec, backend, name, false, false, false, "Not compiled into this FFmpeg build."));
                    continue;
                }
                if (backend == EncoderBackend.Vaapi && !File.Exists(vaapiDevice ?? VideoEncoders.DefaultVaapiDevice))
                {
                    results.Add(new(codec, backend, name, true, false, false, "No VA-API render device."));
                    continue;
                }
                log?.Report($"Testing {name}…");
                var (ok8, err8) = await TestAsync(ffmpeg, codec, backend, false, vaapiDevice, ct);
                bool ok10 = false;
                string? err10 = null;
                if (VideoEncoders.Supports10Bit(codec, backend))
                    (ok10, err10) = await TestAsync(ffmpeg, codec, backend, true, vaapiDevice, ct);
                log?.Report($"  {name}: 8-bit {(ok8 ? "OK" : "no")}" +
                            (VideoEncoders.Supports10Bit(codec, backend) ? $", 10-bit {(ok10 ? "OK" : "no")}" : ""));
                results.Add(new(codec, backend, name, true, ok8, ok10, ok8 || ok10 ? null : err8 ?? err10));
            }

        return new FfmpegCapabilities(version, results, libass, DateTimeOffset.Now)
        {
            HasZscale = zscale,
            HasLibplacebo = placebo,
            HasLibwebp = compiled.Contains("libwebp"),
        };
    }

    private static async Task<(bool, string?)> TestAsync(string ffmpeg, VideoCodec codec, EncoderBackend backend,
        bool tenBit, string? vaapiDevice, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin" };
        args.AddRange(VideoEncoders.GlobalArgs(backend, vaapiDevice));
        args.AddRange(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=0.5"]);
        if (VideoEncoders.FilterTail(backend, codec, tenBit) is { } tail) args.AddRange(["-vf", tail]);
        args.AddRange(VideoEncoders.BuildArgs(new VideoEncodeRequest(codec, backend, QualityLevel.Normal, tenBit,
            ProfileKind.UniversalMkv, 25, false)));
        args.AddRange(["-frames:v", "10", "-f", "null", "-"]);
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, args, timeout: TimeSpan.FromSeconds(30), ct: ct);
            var err = r.StdErr.Trim();
            return (r.ExitCode == 0, r.ExitCode == 0 ? null : err.Split('\n').LastOrDefault(l => l.Length > 0) ?? $"exit {r.ExitCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
