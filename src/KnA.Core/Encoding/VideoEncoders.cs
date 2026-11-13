using System.Globalization;

namespace KnA.Core.Encoding;

/// <summary>Professional codec options (ProRes / DNxHR profile, chroma, bit depth).</summary>
public sealed record ProSettings
{
    public ProResProfile ProRes { get; init; } = ProResProfile.Auto;
    public DnxhrProfile Dnxhr { get; init; } = DnxhrProfile.Auto;
    public ChromaFormat Chroma { get; init; } = ChromaFormat.Auto;
    /// <summary>0 = automatic (codec default / source depth), otherwise 8, 10, 12 or 16.</summary>
    public int BitDepth { get; init; }
}

/// <summary>Request for video encoder arguments.</summary>
public sealed record VideoEncodeRequest(
    VideoCodec Codec,
    EncoderBackend Backend,
    QualityLevel Quality,
    bool TenBit,
    ProfileKind Profile,
    double FrameRate,
    bool Mp4Container)
{
    /// <summary>Exact FFmpeg encoder (one of <see cref="VideoEncoders.Candidates"/>); null = the first candidate.</summary>
    public string? Encoder { get; init; }
    public ProSettings Pro { get; init; } = new();
    /// <summary>Bit depth of the source (lossless codecs keep it when nothing else is asked).</summary>
    public int SourceBitDepth { get; init; } = 8;
}

/// <summary>
/// Knows every FFmpeg encoder KnA can drive: candidates per codec × backend (in order of preference,
/// e.g. AV1 on the CPU = SVT-AV1 → libaom → rav1e), pixel formats and rate-control flags.
/// Which candidate is really used is decided at runtime by <see cref="Tools.HardwareDetector"/>, which
/// test-encodes each one with exactly these arguments (so a custom FFmpeg build with extra encoders,
/// or a missing GPU driver, is handled without guessing).
///
/// Quality scales differ per encoder: x264/x265/SVT-AV1/libvpx use CRF (lower = better), NVENC CQ,
/// QSV ICQ, VA-API/Vulkan QP (lower = better), AMF QVBR (HIGHER = better), VideoToolbox -q:v 0..100 (higher = better).
/// </summary>
public static class VideoEncoders
{
    public static IReadOnlyList<EncoderBackend> AllBackends { get; } = Enum.GetValues<EncoderBackend>();

    /// <summary>FFmpeg encoders for a codec on a backend, best first; empty when the combination doesn't exist.</summary>
    public static IReadOnlyList<string> Candidates(VideoCodec codec, EncoderBackend backend) => (codec, backend) switch
    {
        (VideoCodec.H264, EncoderBackend.Software) => ["libx264"],
        (VideoCodec.HEVC, EncoderBackend.Software) => ["libx265"],
        (VideoCodec.AV1, EncoderBackend.Software) => ["libsvtav1", "libaom-av1", "librav1e"],
        (VideoCodec.Xvid, EncoderBackend.Software) => ["libxvid", "mpeg4"],
        (VideoCodec.VP9, EncoderBackend.Software) => ["libvpx-vp9"],
        (VideoCodec.VVC, EncoderBackend.Software) => ["libvvenc"],
        (VideoCodec.MPEG2, EncoderBackend.Software) => ["mpeg2video"],
        (VideoCodec.ProRes, EncoderBackend.Software) => ["prores_ks"],
        (VideoCodec.DNxHR, EncoderBackend.Software) => ["dnxhd"],
        (VideoCodec.CineForm, EncoderBackend.Software) => ["cfhd"],
        (VideoCodec.FFV1, EncoderBackend.Software) => ["ffv1"],
        (VideoCodec.UtVideo, EncoderBackend.Software) => ["utvideo"],
        (VideoCodec.FFVHuff, EncoderBackend.Software) => ["ffvhuff"],
        (VideoCodec.JPEG2000, EncoderBackend.Software) => ["libopenjpeg", "jpeg2000"],
        (VideoCodec.Uncompressed, EncoderBackend.Software) => ["v210"],

        (VideoCodec.H264, EncoderBackend.Nvenc) => ["h264_nvenc"],
        (VideoCodec.HEVC, EncoderBackend.Nvenc) => ["hevc_nvenc"],
        (VideoCodec.AV1, EncoderBackend.Nvenc) => ["av1_nvenc"],

        (VideoCodec.H264, EncoderBackend.Qsv) => ["h264_qsv"],
        (VideoCodec.HEVC, EncoderBackend.Qsv) => ["hevc_qsv"],
        (VideoCodec.AV1, EncoderBackend.Qsv) => ["av1_qsv"],
        (VideoCodec.VP9, EncoderBackend.Qsv) => ["vp9_qsv"],
        (VideoCodec.MPEG2, EncoderBackend.Qsv) => ["mpeg2_qsv"],

        (VideoCodec.H264, EncoderBackend.Amf) => ["h264_amf"],
        (VideoCodec.HEVC, EncoderBackend.Amf) => ["hevc_amf"],
        (VideoCodec.AV1, EncoderBackend.Amf) => ["av1_amf"],

        // Apple Silicon (up to M4) decodes AV1 in hardware but has no AV1 hardware encoder.
        (VideoCodec.H264, EncoderBackend.VideoToolbox) => ["h264_videotoolbox"],
        (VideoCodec.HEVC, EncoderBackend.VideoToolbox) => ["hevc_videotoolbox"],
        (VideoCodec.ProRes, EncoderBackend.VideoToolbox) => ["prores_videotoolbox"],

        (VideoCodec.H264, EncoderBackend.Vaapi) => ["h264_vaapi"],
        (VideoCodec.HEVC, EncoderBackend.Vaapi) => ["hevc_vaapi"],
        (VideoCodec.AV1, EncoderBackend.Vaapi) => ["av1_vaapi"],
        (VideoCodec.VP9, EncoderBackend.Vaapi) => ["vp9_vaapi"],
        (VideoCodec.MPEG2, EncoderBackend.Vaapi) => ["mpeg2_vaapi"],

        (VideoCodec.H264, EncoderBackend.Vulkan) => ["h264_vulkan"],
        (VideoCodec.HEVC, EncoderBackend.Vulkan) => ["hevc_vulkan"],
        (VideoCodec.AV1, EncoderBackend.Vulkan) => ["av1_vulkan"],
        (VideoCodec.FFV1, EncoderBackend.Vulkan) => ["ffv1_vulkan"],
        (VideoCodec.ProRes, EncoderBackend.Vulkan) => ["prores_vulkan"],
        _ => [],
    };

    /// <summary>Preferred FFmpeg encoder name, or null when the combination doesn't exist.</summary>
    public static string? EncoderName(VideoCodec codec, EncoderBackend backend) => Candidates(codec, backend).FirstOrDefault();

    /// <summary>Backends that have at least one encoder for the codec.</summary>
    public static IEnumerable<EncoderBackend> BackendsFor(VideoCodec codec) => AllBackends.Where(b => Candidates(codec, b).Count > 0);

    /// <summary>Whether a "10-bit" variant is offered (professional codecs decide their depth themselves).</summary>
    public static bool Supports10Bit(VideoCodec codec, EncoderBackend backend) =>
        Candidates(codec, backend).Count > 0 && codec switch
        {
            VideoCodec.HEVC or VideoCodec.AV1 or VideoCodec.VVC => true,
            VideoCodec.VP9 => backend is EncoderBackend.Software or EncoderBackend.Qsv or EncoderBackend.Vaapi,
            VideoCodec.H264 => false,
            _ => codec.IsProfessional(),
        };

    /// <summary>Codecs that only exist in a 10-bit form here (VVC: vvenc works on 10-bit internally).</summary>
    public static bool Always10Bit(VideoCodec codec) => codec is VideoCodec.VVC;

    /// <summary>Pixel format for delivery codecs on a backend (hardware takes semi-planar formats).</summary>
    public static string PixelFormat(EncoderBackend backend, bool tenBit) => (backend, tenBit) switch
    {
        (EncoderBackend.Software, false) => "yuv420p",
        (EncoderBackend.Software, true) => "yuv420p10le",
        (_, false) => "nv12",
        (_, true) => "p010le",
    };

    public static List<string> BuildArgs(VideoEncodeRequest r)
    {
        var candidates = Candidates(r.Codec, r.Backend);
        var name = r.Encoder is { } e && candidates.Contains(e) ? e
                   : candidates.FirstOrDefault()
                     ?? throw new NotSupportedException($"{r.Codec.Short()} is not available with {r.Backend.Display()}.");
        bool tenBit = Always10Bit(r.Codec) || (r.TenBit && Supports10Bit(r.Codec, r.Backend));
        int q = (int)r.Quality;
        var a = new List<string> { "-c:v", name };
        string? pixFmt = r.Codec.IsProfessional() ? null : PixelFormat(r.Backend, tenBit);

        switch (name)
        {
            // ---------------------------------------------------------------- CPU, delivery
            case "libx264":
            {
                double crf = r.Profile == ProfileKind.Workraw
                    ? Pick(q, 20, 23, 26, 29, 32, 35, 38, 45)
                    : Pick(q, 14, 17, 18.5, 20, 22, 25, 27, 36);
                a.AddRange(["-preset", Pick(q, "veryslow", "veryslow", "slower", "medium", "fast", "fast", "faster", "superfast"),
                    "-crf", Num(crf), "-aq-mode", Num(Pick(q, 2, 2, 2, 3, 3, 3, 3, 1))]);
                break;
            }
            case "libx265":
                a.AddRange(["-preset", Pick(q, "slower", "slow", "medium", "medium", "fast", "faster", "veryfast", "ultrafast"),
                    "-crf", Num(Pick(q, 17, 20, 21.5, 23, 25, 28, 30, 39)),
                    "-x265-params", "aq-mode=3:log-level=error"]);
                if (tenBit) a.AddRange(["-profile:v", "main10"]);
                break;
            case "libsvtav1":
                a.AddRange(["-preset", Num(Pick(q, 3, 4, 5, 6, 8, 9, 10, 12)),
                    "-crf", Num(Pick(q, 18, 22, 25, 28, 32, 36, 40, 50))]);
                break;
            case "libaom-av1":
                a.AddRange(["-cpu-used", Num(Pick(q, 3, 4, 4, 5, 6, 6, 7, 8)), "-row-mt", "1", "-b:v", "0",
                    "-crf", Num(Pick(q, 18, 22, 25, 28, 32, 36, 40, 50))]);
                break;
            case "librav1e":
                a.AddRange(["-speed", Num(Pick(q, 4, 5, 6, 6, 7, 8, 9, 10)),
                    "-qp", Num(Pick(q, 50, 65, 75, 85, 95, 110, 125, 160))]);
                break;
            case "libxvid":
                a.AddRange(["-q:v", Num(Pick(q, 1, 2, 3, 4, 5, 6, 7, 10)), "-vtag", "XVID", "-bf", "2",
                    "-mbd", "rd", "-flags", "+mv4+aic", "-trellis", "2", "-qmin", "1", "-qmax", "10"]);
                break;
            case "mpeg4":
                a.AddRange(["-q:v", Num(Pick(q, 2, 2, 3, 4, 5, 6, 7, 10)), "-vtag", "XVID", "-bf", "2", "-mbd", "rd", "-flags", "+mv4+aic"]);
                break;
            case "libvpx-vp9":
                a.AddRange(["-crf", Num(Pick(q, 18, 24, 28, 31, 34, 38, 42, 50)), "-b:v", "0", "-row-mt", "1",
                    "-deadline", "good", "-cpu-used", Num(Pick(q, 1, 2, 2, 3, 4, 4, 5, 8)), "-tile-columns", "2"]);
                if (tenBit) a.AddRange(["-profile:v", "2"]);
                break;
            case "libvvenc":
                a.AddRange(["-preset", Pick(q, "slower", "slow", "medium", "medium", "fast", "fast", "faster", "faster"),
                    "-qp", Num(Pick(q, 22, 26, 29, 32, 34, 37, 40, 46))]);
                break;
            case "mpeg2video":
                a.AddRange(["-q:v", Num(Pick(q, 2, 2, 3, 4, 5, 6, 8, 12)), "-g", Num(Math.Clamp((int)Math.Round(r.FrameRate / 2), 12, 18)),
                    "-bf", "2", "-mbd", "rd", "-trellis", "1"]);
                break;

            // ---------------------------------------------------------------- CPU, professional
            case "prores_ks":
            {
                var prof = ProResFor(r);
                a.AddRange(["-profile:v", ProResNumber(prof), "-vendor", "apl0"]);
                if (prof >= ProResProfile.P4444) a.AddRange(["-alpha_bits", "0"]);
                pixFmt = prof >= ProResProfile.P4444 ? "yuv444p10le" : "yuv422p10le";
                break;
            }
            case "dnxhd":
            {
                var prof = DnxFor(r);
                a.AddRange(["-profile:v", prof switch
                {
                    DnxhrProfile.Lb => "dnxhr_lb", DnxhrProfile.Sq => "dnxhr_sq", DnxhrProfile.Hq => "dnxhr_hq",
                    DnxhrProfile.Hqx => "dnxhr_hqx", _ => "dnxhr_444",
                }]);
                pixFmt = prof switch { DnxhrProfile.Hqx => "yuv422p10le", DnxhrProfile.P444 => "yuv444p10le", _ => "yuv422p" };
                break;
            }
            case "cfhd":
                a.AddRange(["-quality", Pick(q, "film3+", "film2+", "film1++", "film1", "high+", "medium+", "medium", "low")]);
                pixFmt = r.Pro.Chroma == ChromaFormat.Yuv444 ? "gbrp12le" : "yuv422p10le";
                break;
            case "ffv1":
                a.AddRange(["-level", "3", "-coder", "1", "-context", "1", "-g", "1", "-slices", "24", "-slicecrc", "1"]);
                pixFmt = LosslessPixFmt(r, maxDepth: 16);
                break;
            case "utvideo":
                a.AddRange(["-pred", "median"]);
                pixFmt = LosslessPixFmt(r, maxDepth: 8);
                break;
            case "ffvhuff":
                a.AddRange(["-context", "1"]);
                pixFmt = LosslessPixFmt(r, maxDepth: 16);
                break;
            case "libopenjpeg":
                // Reversible 5/3 wavelet = lossless.
                a.AddRange(["-compression_level", "0"]);
                pixFmt = LosslessPixFmt(r, maxDepth: 16);
                break;
            case "jpeg2000":
                a.AddRange(["-pred", "dwt53", "-strict", "experimental"]);
                pixFmt = LosslessPixFmt(r, maxDepth: 8);
                break;
            case "v210":
                pixFmt = "yuv422p10le";
                break;

            // ---------------------------------------------------------------- NVIDIA
            case "h264_nvenc" or "hevc_nvenc" or "av1_nvenc":
                a.AddRange(["-preset", Pick(q, "p7", "p7", "p6", "p5", "p4", "p3", "p2", "p1"), "-tune", "hq",
                    "-rc", "vbr", "-cq", Num(Pick(q, NvencCq[r.Codec])), "-b:v", "0",
                    "-spatial-aq", "1", "-rc-lookahead", "32"]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;

            // ---------------------------------------------------------------- Intel QSV
            case "h264_qsv" or "hevc_qsv" or "av1_qsv" or "vp9_qsv" or "mpeg2_qsv":
                a.AddRange(["-preset", Pick(q, "veryslow", "slower", "slow", "medium", "medium", "fast", "faster", "veryfast"),
                    "-global_quality", Num(Pick(q, QsvIcq.GetValueOrDefault(r.Codec, QsvIcq[VideoCodec.HEVC])))]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;

            // ---------------------------------------------------------------- AMD AMF
            case "h264_amf" or "hevc_amf" or "av1_amf":
                // QVBR level: 1 = worst, 51 = best (inverse of CRF!).
                a.AddRange(["-quality", "quality", "-rc", "qvbr", "-qvbr_quality_level", Num(Pick(q, AmfQvbr))]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;

            // ---------------------------------------------------------------- Apple
            case "h264_videotoolbox" or "hevc_videotoolbox":
                a.AddRange(["-q:v", Num(Pick(q, 80, 72, 66, 60, 55, 50, 45, 35)), "-allow_sw", "0"]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                break;
            case "prores_videotoolbox":
            {
                var prof = ProResFor(r);
                a.AddRange(["-profile:v", prof switch
                {
                    ProResProfile.Proxy => "proxy", ProResProfile.Lt => "lt", ProResProfile.Standard => "standard",
                    ProResProfile.Hq => "hq", ProResProfile.P4444 => "4444", _ => "xq",
                }, "-allow_sw", "0"]);
                pixFmt = prof >= ProResProfile.P4444 ? "ayuv64le" : "p210le";
                break;
            }

            // ---------------------------------------------------------------- VA-API
            case "h264_vaapi" or "hevc_vaapi" or "av1_vaapi" or "vp9_vaapi" or "mpeg2_vaapi":
                // AV1/VP9 use a 0..255 quantizer index, H.264/HEVC/MPEG-2 a 1..51 QP.
                a.AddRange(["-rc_mode", "CQP", "-global_quality", Num(Pick(q, VaapiQp.GetValueOrDefault(r.Codec, VaapiQp[VideoCodec.HEVC])))]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                pixFmt = null; // frames are uploaded by the filter chain (FilterTail)
                break;

            // ---------------------------------------------------------------- Vulkan
            case "h264_vulkan" or "hevc_vulkan" or "av1_vulkan":
                a.AddRange(["-rc_mode", "cqp", "-qp", Num(Pick(q, VulkanQp.GetValueOrDefault(r.Codec, VulkanQp[VideoCodec.HEVC])))]);
                if (tenBit && r.Codec == VideoCodec.HEVC) a.AddRange(["-profile:v", "main10"]);
                pixFmt = null;
                break;
            case "ffv1_vulkan":
                a.AddRange(["-level", "3", "-slicecrc", "1"]);
                pixFmt = null;
                break;
            case "prores_vulkan":
                a.AddRange(["-profile:v", ProResNumber(ProResFor(r))]);
                pixFmt = null;
                break;
        }

        AddProfileConstraints(a, r, name);
        if (pixFmt is not null && !IsGpuUploaded(r.Backend)) a.AddRange(["-pix_fmt", pixFmt]);
        if (r.Codec == VideoCodec.HEVC && r.Mp4Container) a.AddRange(["-tag:v", "hvc1"]);
        return a;
    }

    /// <summary>ProRes profile: explicit, or derived from quality and chroma.</summary>
    public static ProResProfile ProResFor(VideoEncodeRequest r)
    {
        if (r.Pro.ProRes != ProResProfile.Auto) return r.Pro.ProRes;
        if (r.Pro.Chroma == ChromaFormat.Yuv444) return r.Quality <= QualityLevel.VeryHigh ? ProResProfile.P4444Xq : ProResProfile.P4444;
        return r.Quality switch
        {
            QualityLevel.VeryHigh or QualityLevel.High => ProResProfile.Hq,
            QualityLevel.AboveNormal or QualityLevel.Normal => ProResProfile.Standard,
            QualityLevel.BelowNormal or QualityLevel.Low => ProResProfile.Lt,
            _ => ProResProfile.Proxy,
        };
    }

    public static DnxhrProfile DnxFor(VideoEncodeRequest r)
    {
        if (r.Pro.Dnxhr != DnxhrProfile.Auto) return r.Pro.Dnxhr;
        if (r.Pro.Chroma == ChromaFormat.Yuv444) return DnxhrProfile.P444;
        if (r.Pro.BitDepth >= 10) return DnxhrProfile.Hqx;
        return r.Quality switch
        {
            QualityLevel.VeryHigh => DnxhrProfile.Hqx,
            QualityLevel.High or QualityLevel.AboveNormal => DnxhrProfile.Hq,
            QualityLevel.Normal or QualityLevel.BelowNormal => DnxhrProfile.Sq,
            _ => DnxhrProfile.Lb,
        };
    }

    private static string ProResNumber(ProResProfile p) => p switch
    {
        ProResProfile.Proxy => "0", ProResProfile.Lt => "1", ProResProfile.Standard => "2",
        ProResProfile.Hq => "3", ProResProfile.P4444 => "4", _ => "5",
    };

    /// <summary>Lossless codecs: keep the source depth unless asked otherwise; chroma 4:2:0 unless asked.</summary>
    private static string LosslessPixFmt(VideoEncodeRequest r, int maxDepth)
    {
        int depth = r.Pro.BitDepth > 0 ? r.Pro.BitDepth : Math.Max(8, r.SourceBitDepth);
        depth = depth switch { <= 8 => 8, <= 10 => 10, <= 12 => 12, _ => 16 };
        depth = Math.Min(depth, maxDepth);
        string chroma = r.Pro.Chroma switch { ChromaFormat.Yuv422 => "422", ChromaFormat.Yuv444 => "444", _ => "420" };
        return depth == 8 ? $"yuv{chroma}p" : $"yuv{chroma}p{depth}le";
    }

    private static bool IsGpuUploaded(EncoderBackend b) => b is EncoderBackend.Vaapi or EncoderBackend.Vulkan;

    private static void AddProfileConstraints(List<string> a, VideoEncodeRequest r, string encoder)
    {
        int gop = Math.Max(1, (int)Math.Ceiling(r.FrameRate > 0 ? r.FrameRate : 24) * 2);
        string g = gop.ToString(CultureInfo.InvariantCulture);
        switch (r.Profile)
        {
            case ProfileKind.BlurayAac or ProfileKind.BlurayAc3:
                // Blu-ray spec: video max 40 Mb/s, VBV buffer 30 000 kbit, H.264 High@4.1.
                a.AddRange(["-profile:v", "high", "-level:v", "4.1", "-maxrate", "40000k", "-bufsize", "30000k"]);
                if (encoder == "libx264") a.AddRange(["-bluray-compat", "1"]);
                break;
            case ProfileKind.Streaming:
                if (r.Codec == VideoCodec.H264) a.AddRange(["-profile:v", "high", "-level:v", "4.1"]);
                if (r.Backend is not (EncoderBackend.Qsv or EncoderBackend.Vulkan)) a.AddRange(["-maxrate", "20000k", "-bufsize", "20000k"]);
                a.AddRange(["-g", g]);
                if (encoder is not ("libvpx-vp9" or "libsvtav1" or "libaom-av1" or "librav1e")) a.AddRange(["-keyint_min", g]);
                break;
            case ProfileKind.Workraw:
                a.AddRange(["-profile:v", "high", "-level:v", "4.1"]);
                if (encoder == "libx264") a.AddRange(["-tune", "fastdecode"]);
                if (r.Backend is not (EncoderBackend.Qsv or EncoderBackend.Vulkan)) a.AddRange(["-maxrate", "20000k", "-bufsize", "20000k"]);
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
        EncoderBackend.Vulkan => ["-init_hw_device", "vulkan=vk", "-filter_hw_device", "vk"],
        _ => [],
    };

    /// <summary>Filters that must end the video chain (e.g. upload to GPU surfaces), or null.</summary>
    public static string? FilterTail(EncoderBackend backend, VideoCodec codec, bool tenBit) => backend switch
    {
        EncoderBackend.Vaapi => (tenBit && Supports10Bit(codec, backend) ? "format=p010" : "format=nv12") + ",hwupload",
        EncoderBackend.Vulkan when codec is VideoCodec.FFV1 => "hwupload",
        EncoderBackend.Vulkan when codec is VideoCodec.ProRes => "format=yuv422p10le,hwupload",
        EncoderBackend.Vulkan => (tenBit && Supports10Bit(codec, backend) ? "format=p010" : "format=nv12") + ",hwupload",
        _ => null,
    };

    public const string DefaultVaapiDevice = "/dev/dri/renderD128";

    private static readonly Dictionary<VideoCodec, double[]> VaapiQp = new()
    {
        [VideoCodec.H264] = [17, 19, 21, 23, 25, 28, 31, 38],
        [VideoCodec.HEVC] = [19, 21, 23, 25, 27, 30, 33, 40],
        [VideoCodec.MPEG2] = [3, 4, 5, 6, 8, 10, 12, 16],
        [VideoCodec.AV1] = [72, 88, 100, 112, 124, 140, 156, 184],
        [VideoCodec.VP9] = [60, 76, 88, 100, 112, 128, 144, 176],
    };

    private static readonly Dictionary<VideoCodec, double[]> VulkanQp = new()
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
        [VideoCodec.VP9] = [19, 21, 23, 25, 27, 30, 33, 40],
        [VideoCodec.MPEG2] = [16, 18, 20, 22, 24, 27, 30, 36],
    };

    private static readonly double[] AmfQvbr = [36, 33, 31, 29, 27, 24, 21, 14];

    private static T Pick<T>(int index, params T[] values) => values[Math.Clamp(index, 0, values.Length - 1)];

    internal static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
