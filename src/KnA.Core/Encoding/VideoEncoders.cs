using System.Globalization;

namespace KnA.Core.Encoding;

/// <summary>Request for video encoder arguments.</summary>
public sealed record VideoEncodeRequest(
    VideoCodec Codec,
    EncoderBackend Backend,
    QualityLevel Quality,
    bool TenBit,
    ProfileKind Profile,
    double FrameRate,
    bool Mp4Container);

/// <summary>
/// Knows the FFmpeg encoder name, pixel format and rate-control flags for every
/// codec × backend combination. Quality scales differ per encoder:
/// x264/x265/SVT-AV1 use CRF (lower = better), NVENC uses CQ (lower = better),
/// QSV uses ICQ global_quality (lower = better), AMF uses QVBR level (HIGHER = better),
/// VideoToolbox uses -q:v 0..100 (higher = better).
/// </summary>
public static class VideoEncoders
{
    public static IReadOnlyList<EncoderBackend> AllBackends { get; } = Enum.GetValues<EncoderBackend>();

    /// <summary>FFmpeg encoder name, or null when the combination doesn't exist.</summary>
    public static string? EncoderName(VideoCodec codec, EncoderBackend backend) => (codec, backend) switch
    {
        (VideoCodec.H264, EncoderBackend.Software) => "libx264",
        (VideoCodec.HEVC, EncoderBackend.Software) => "libx265",
        (VideoCodec.AV1, EncoderBackend.Software) => "libsvtav1",
        (VideoCodec.Xvid, EncoderBackend.Software) => "libxvid",
        (VideoCodec.H264, EncoderBackend.Nvenc) => "h264_nvenc",
        (VideoCodec.HEVC, EncoderBackend.Nvenc) => "hevc_nvenc",
        (VideoCodec.AV1, EncoderBackend.Nvenc) => "av1_nvenc",
        (VideoCodec.H264, EncoderBackend.Qsv) => "h264_qsv",
        (VideoCodec.HEVC, EncoderBackend.Qsv) => "hevc_qsv",
        (VideoCodec.AV1, EncoderBackend.Qsv) => "av1_qsv",
        (VideoCodec.H264, EncoderBackend.Amf) => "h264_amf",
        (VideoCodec.HEVC, EncoderBackend.Amf) => "hevc_amf",
        (VideoCodec.AV1, EncoderBackend.Amf) => "av1_amf",
        (VideoCodec.H264, EncoderBackend.VideoToolbox) => "h264_videotoolbox",
        (VideoCodec.HEVC, EncoderBackend.VideoToolbox) => "hevc_videotoolbox",
        (VideoCodec.H264, EncoderBackend.Vaapi) => "h264_vaapi",
        (VideoCodec.HEVC, EncoderBackend.Vaapi) => "hevc_vaapi",
        (VideoCodec.AV1, EncoderBackend.Vaapi) => "av1_vaapi",
        // Apple Silicon (up to M4) can decode AV1 in hardware but has no AV1 hardware encoder.
        _ => null,
    };

    /// <summary>Whether 10-bit output is offered for this codec/backend (still verified at runtime).</summary>
    public static bool Supports10Bit(VideoCodec codec, EncoderBackend backend) =>
        codec is VideoCodec.HEVC or VideoCodec.AV1 && EncoderName(codec, backend) is not null;

    public static string PixelFormat(EncoderBackend backend, bool tenBit) => (backend, tenBit) switch
    {
        (EncoderBackend.Software, false) => "yuv420p",
        (EncoderBackend.Software, true) => "yuv420p10le",
        // Hardware encoders take semi-planar formats from system memory.
        (_, false) => "nv12",
        (_, true) => "p010le",
    };

    public static List<string> BuildArgs(VideoEncodeRequest r)
    {
        var name = EncoderName(r.Codec, r.Backend)
                   ?? throw new NotSupportedException($"{r.Codec.Short()} is not available with {r.Backend.Display()}.");
        bool tenBit = r.TenBit && Supports10Bit(r.Codec, r.Backend);
        int q = (int)r.Quality;
        var a = new List<string> { "-c:v", name };

        switch (r.Backend)
        {
            case EncoderBackend.Software:
                AddSoftware(a, r, tenBit, q);
                break;
            case EncoderBackend.Nvenc:
                a.AddRange(["-preset", Pick(q, "p7", "p7", "p6", "p5", "p4", "p3", "p2", "p1"), "-tune", "hq",
                    "-rc", "vbr", "-cq", Num(Pick(q, NvencCq[r.Codec])), "-b:v", "0",
                    "-spatial-aq", "1", "-rc-lookahead", "32"]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;
            case EncoderBackend.Qsv:
                a.AddRange(["-preset", Pick(q, "veryslow", "slower", "slow", "medium", "medium", "fast", "faster", "veryfast"),
                    "-global_quality", Num(Pick(q, QsvIcq[r.Codec]))]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;
            case EncoderBackend.Amf:
                // QVBR level: 1 = worst, 51 = best (inverse of CRF!).
                a.AddRange(["-quality", "quality", "-rc", "qvbr", "-qvbr_quality_level", Num(Pick(q, AmfQvbr[r.Codec]))]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;
            case EncoderBackend.VideoToolbox:
                a.AddRange(["-q:v", Num(Pick(q, 80, 72, 66, 60, 55, 50, 45, 35)), "-allow_sw", "0"]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;
            case EncoderBackend.Vaapi:
                // AV1 uses a 0..255 quantizer index, H.264/HEVC a 1..51 QP.
                a.AddRange(["-rc_mode", "CQP", "-global_quality", Num(Pick(q, VaapiQp[r.Codec]))]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;
        }

        AddProfileConstraints(a, r);
        // VA-API frames are uploaded by the filter chain (see FilterTail), so no -pix_fmt there.
        if (r.Backend != EncoderBackend.Vaapi) a.AddRange(["-pix_fmt", PixelFormat(r.Backend, tenBit)]);
        if (r.Codec == VideoCodec.HEVC && r.Mp4Container) a.AddRange(["-tag:v", "hvc1"]);
        return a;
    }

    private static void AddSoftware(List<string> a, VideoEncodeRequest r, bool tenBit, int q)
    {
        switch (r.Codec)
        {
            case VideoCodec.H264:
                double crf = r.Profile == ProfileKind.Workraw
                    ? Pick(q, 20, 23, 26, 29, 32, 35, 38, 45)
                    : Pick(q, 14, 17, 18.5, 20, 22, 25, 27, 36);
                a.AddRange(["-preset", Pick(q, "veryslow", "veryslow", "slower", "medium", "fast", "fast", "faster", "superfast"),
                    "-crf", Num(crf), "-aq-mode", Num(Pick(q, 2, 2, 2, 3, 3, 3, 3, 1))]);
                break;
            case VideoCodec.HEVC:
                a.AddRange(["-preset", Pick(q, "slower", "slow", "medium", "medium", "fast", "faster", "veryfast", "ultrafast"),
                    "-crf", Num(Pick(q, 17, 20, 21.5, 23, 25, 28, 30, 39)),
                    "-x265-params", "aq-mode=3:log-level=error"]);
                if (tenBit) a.AddRange(["-profile:v", "main10"]);
                break;
            case VideoCodec.AV1:
                a.AddRange(["-preset", Num(Pick(q, 3, 4, 5, 6, 8, 9, 10, 12)),
                    "-crf", Num(Pick(q, 18, 22, 25, 28, 32, 36, 40, 50))]);
                break;
            case VideoCodec.Xvid:
                a.AddRange(["-q:v", Num(Pick(q, 1, 2, 3, 4, 5, 6, 7, 10)), "-vtag", "XVID", "-bf", "2",
                    "-mbd", "rd", "-flags", "+mv4+aic", "-trellis", "2", "-qmin", "1", "-qmax", "10"]);
                break;
        }
    }

    private static void AddProfileConstraints(List<string> a, VideoEncodeRequest r)
    {
        int gop = Math.Max(1, (int)Math.Ceiling(r.FrameRate > 0 ? r.FrameRate : 24) * 2);
        switch (r.Profile)
        {
            case ProfileKind.BlurayAac or ProfileKind.BlurayAc3:
                // Blu-ray spec: video max 40 Mb/s, VBV buffer 30 000 kbit, H.264 High@4.1.
                a.AddRange(["-profile:v", "high", "-level:v", "4.1", "-maxrate", "40000k", "-bufsize", "30000k"]);
                if (r.Backend == EncoderBackend.Software) a.AddRange(["-bluray-compat", "1"]);
                break;
            case ProfileKind.Streaming:
                if (r.Codec == VideoCodec.H264) a.AddRange(["-profile:v", "high", "-level:v", "4.1"]);
                if (r.Backend != EncoderBackend.Qsv) a.AddRange(["-maxrate", "20000k", "-bufsize", "20000k"]);
                a.AddRange(["-g", gop.ToString(CultureInfo.InvariantCulture), "-keyint_min", gop.ToString(CultureInfo.InvariantCulture)]);
                break;
            case ProfileKind.Workraw:
                a.AddRange(["-profile:v", "high", "-level:v", "4.1"]);
                if (r.Backend == EncoderBackend.Software) a.AddRange(["-tune", "fastdecode"]);
                if (r.Backend != EncoderBackend.Qsv) a.AddRange(["-maxrate", "20000k", "-bufsize", "20000k"]);
                break;
            case ProfileKind.RemuxMp4:
                if (r.Codec == VideoCodec.H264) a.AddRange(["-profile:v", "high", "-level:v", "4.1"]);
                break;
        }
    }

    /// <summary>Global options that must precede the input (hardware device setup).</summary>
    public static List<string> GlobalArgs(EncoderBackend backend, string? vaapiDevice) => backend switch
    {
        EncoderBackend.Vaapi => ["-init_hw_device", $"vaapi=va:{vaapiDevice ?? DefaultVaapiDevice}", "-filter_hw_device", "va"],
        _ => [],
    };

    /// <summary>Filters that must end the video chain (e.g. upload to GPU surfaces), or null.</summary>
    public static string? FilterTail(EncoderBackend backend, VideoCodec codec, bool tenBit) => backend switch
    {
        EncoderBackend.Vaapi => (tenBit && Supports10Bit(codec, backend) ? "format=p010" : "format=nv12") + ",hwupload",
        _ => null,
    };

    public const string DefaultVaapiDevice = "/dev/dri/renderD128";

    private static readonly Dictionary<VideoCodec, double[]> VaapiQp = new()
    {
        [VideoCodec.H264] = [17, 19, 21, 23, 25, 28, 31, 38],
        [VideoCodec.HEVC] = [19, 21, 23, 25, 27, 30, 33, 40],
        [VideoCodec.AV1] = [72, 88, 100, 112, 124, 140, 156, 184],
    };

    private static readonly Dictionary<VideoCodec, double[]> NvencCq = new()
    {
        [VideoCodec.H264] = [16, 19, 21, 23, 25, 28, 31, 38],
        [VideoCodec.HEVC] = [18, 21, 23, 25, 27, 30, 33, 40],
        // Kept ≤ 51 so it also works on FFmpeg builds older than the 0..63 range fix (May 2024).
        [VideoCodec.AV1] = [20, 24, 27, 30, 33, 37, 41, 48],
    };

    private static readonly Dictionary<VideoCodec, double[]> QsvIcq = new()
    {
        [VideoCodec.H264] = [18, 20, 22, 24, 26, 29, 32, 38],
        [VideoCodec.HEVC] = [19, 21, 23, 25, 27, 30, 33, 40],
        [VideoCodec.AV1] = [19, 21, 23, 25, 27, 30, 33, 40],
    };

    private static readonly Dictionary<VideoCodec, double[]> AmfQvbr = new()
    {
        [VideoCodec.H264] = [36, 33, 31, 29, 27, 24, 21, 14],
        [VideoCodec.HEVC] = [36, 33, 31, 29, 27, 24, 21, 14],
        [VideoCodec.AV1] = [36, 33, 31, 29, 27, 24, 21, 14],
    };

    private static T Pick<T>(int index, params T[] values) => values[Math.Clamp(index, 0, values.Length - 1)];

    internal static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
