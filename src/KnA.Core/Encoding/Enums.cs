using KnA.Core.Localization;

namespace KnA.Core.Encoding;

public enum VideoCodec
{
    H264, HEVC, AV1, Xvid,
    VP9, VVC, MPEG2,
    // ---- professional / intermediate / lossless
    ProRes, DNxHR, CineForm, FFV1, UtVideo, FFVHuff, JPEG2000, Uncompressed,
}

/// <summary>Audio codec of the output tracks. Auto = what the profile wants (copy when possible).</summary>
public enum AudioCodec
{
    Auto, Copy,
    Aac, HeAac, Opus, Mp3, Vorbis, Ac3, Eac3,
    Flac, Alac, WavPack, TrueHd, Dts,
    Pcm16, Pcm24, Pcm32Float,
}

public enum ProResProfile { Auto, Proxy, Lt, Standard, Hq, P4444, P4444Xq }
public enum DnxhrProfile { Auto, Lb, Sq, Hq, Hqx, P444 }
public enum ChromaFormat { Auto, Yuv420, Yuv422, Yuv444 }

public enum EncoderBackend
{
    /// <summary>CPU: libx264 / libx265 / libsvtav1 / libxvid.</summary>
    Software,
    /// <summary>NVIDIA NVENC (GeForce / RTX).</summary>
    Nvenc,
    /// <summary>Intel Quick Sync Video (iGPU / Arc).</summary>
    Qsv,
    /// <summary>AMD Advanced Media Framework (Radeon / Ryzen APU).</summary>
    Amf,
    /// <summary>Apple VideoToolbox (Apple Silicon / Intel Mac).</summary>
    VideoToolbox,
    /// <summary>Linux VA-API (Intel / AMD through /dev/dri, e.g. in Docker or Proxmox LXC).</summary>
    Vaapi,
    /// <summary>Vulkan video / compute encoders (FFmpeg 7.1+: H.264/HEVC, 8.0+: AV1 and FFV1, 8.1+: ProRes). Any vendor.</summary>
    Vulkan,
}

public enum QualityLevel { VeryHigh, High, AboveNormal, Normal, BelowNormal, Low, VeryLow, Draft }

public enum SubtitleMode
{
    /// <summary>Burn one subtitle track into the picture (fonts from the MKV are used).</summary>
    Hardsub,
    /// <summary>Keep every subtitle track and font as separate streams (forces MKV).</summary>
    Softsub,
    /// <summary>Drop all subtitles.</summary>
    None,
}

public enum ProfileKind
{
    BlurayAac,
    /// <summary>Editing / mezzanine / lossless archive: ProRes, DNxHR, FFV1…</summary>
    Professional,
    BlurayAc3,
    Streaming,
    UniversalMkv,
    XvidMp3,
    Workraw,
    RemuxMp4,
    RemuxMkv,
    Keyframes,
    Images,
    Demux,
}

public enum PostQueueAction { Nothing, CloseApp, Sleep, Shutdown }

/// <summary>How a picture is enlarged when the target resolution is above the source.</summary>
public enum UpscaleMethod
{
    /// <summary>Plain resize (Lanczos / zscale), what every earlier version did.</summary>
    Standard,
    /// <summary>libplacebo EWA Lanczos-sharp on the GPU (Vulkan).</summary>
    Placebo,
    /// <summary>Anime4K CNN shaders through libplacebo (GPU, fast, for animation).</summary>
    Anime4K,
    /// <summary>FSRCNNX neural shader through libplacebo (GPU, live action and animation).</summary>
    Fsrcnnx,
    /// <summary>Real-ESRGAN / Real-CUGAN… models through the KnA AI sidecar (Python, CUDA/ROCm/MPS/CPU).</summary>
    Neural,
}

public static class EnumText
{
    public static string Display(this VideoCodec c) => c switch
    {
        VideoCodec.H264 => L.T("H.264 / AVC"),
        VideoCodec.HEVC => L.T("H.265 / HEVC"),
        VideoCodec.AV1 => L.T("AV1"),
        VideoCodec.Xvid => L.T("MPEG-4 ASP (XviD)"),
        VideoCodec.VP9 => L.T("VP9"),
        VideoCodec.VVC => L.T("H.266 / VVC"),
        VideoCodec.MPEG2 => L.T("MPEG-2 (DVD)"),
        VideoCodec.ProRes => L.T("Apple ProRes"),
        VideoCodec.DNxHR => L.T("Avid DNxHR"),
        VideoCodec.CineForm => L.T("GoPro CineForm"),
        VideoCodec.FFV1 => L.T("FFV1 (lossless archive)"),
        VideoCodec.UtVideo => L.T("Ut Video (lossless)"),
        VideoCodec.FFVHuff => L.T("FFVHuff (lossless)"),
        VideoCodec.JPEG2000 => L.T("JPEG 2000 (lossless)"),
        VideoCodec.Uncompressed => L.T("Uncompressed 10-bit 4:2:2 (v210)"),
        _ => c.ToString(),
    };

    /// <summary>Intra-frame editing / mezzanine / lossless codecs (no GOP, PCM audio by default).</summary>
    public static bool IsProfessional(this VideoCodec c) => c >= VideoCodec.ProRes;

    /// <summary>Mathematically lossless codecs.</summary>
    public static bool IsLossless(this VideoCodec c) =>
        c is VideoCodec.FFV1 or VideoCodec.UtVideo or VideoCodec.FFVHuff or VideoCodec.JPEG2000 or VideoCodec.Uncompressed;

    public static string Display(this AudioCodec a) => a switch
    {
        AudioCodec.Auto => L.T("Automatic (profile)"),
        AudioCodec.Copy => L.T("Copy (no re-encoding)"),
        AudioCodec.Aac => L.T("AAC-LC (best encoder available)"),
        AudioCodec.HeAac => L.T("HE-AAC (low bitrates)"),
        AudioCodec.Opus => "Opus",
        AudioCodec.Mp3 => "MP3",
        AudioCodec.Vorbis => "Vorbis",
        AudioCodec.Ac3 => L.T("Dolby Digital (AC-3)"),
        AudioCodec.Eac3 => L.T("Dolby Digital Plus (E-AC-3)"),
        AudioCodec.Flac => L.T("FLAC (lossless)"),
        AudioCodec.Alac => L.T("Apple Lossless (ALAC)"),
        AudioCodec.WavPack => L.T("WavPack (lossless)"),
        AudioCodec.TrueHd => L.T("Dolby TrueHD (lossless, experimental)"),
        AudioCodec.Dts => L.T("DTS (experimental)"),
        AudioCodec.Pcm16 => L.T("PCM 16-bit"),
        AudioCodec.Pcm24 => L.T("PCM 24-bit"),
        AudioCodec.Pcm32Float => L.T("PCM 32-bit float"),
        _ => a.ToString(),
    };

    public static string Display(this ProResProfile p) => p switch
    {
        ProResProfile.Auto => L.T("Automatic (from quality)"),
        ProResProfile.Proxy => "422 Proxy",
        ProResProfile.Lt => "422 LT",
        ProResProfile.Standard => "422",
        ProResProfile.Hq => "422 HQ",
        ProResProfile.P4444 => "4444",
        ProResProfile.P4444Xq => "4444 XQ",
        _ => p.ToString(),
    };

    public static string Display(this DnxhrProfile p) => p switch
    {
        DnxhrProfile.Auto => L.T("Automatic (from quality)"),
        DnxhrProfile.Lb => "LB (8-bit 4:2:2)",
        DnxhrProfile.Sq => "SQ (8-bit 4:2:2)",
        DnxhrProfile.Hq => "HQ (8-bit 4:2:2)",
        DnxhrProfile.Hqx => "HQX (10-bit 4:2:2)",
        DnxhrProfile.P444 => "444 (10-bit 4:4:4)",
        _ => p.ToString(),
    };

    public static string Display(this ChromaFormat c) => c switch
    {
        ChromaFormat.Auto => L.T("Automatic"),
        ChromaFormat.Yuv420 => "4:2:0",
        ChromaFormat.Yuv422 => "4:2:2",
        ChromaFormat.Yuv444 => "4:4:4",
        _ => c.ToString(),
    };

    public static string Short(this VideoCodec c) => c switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.HEVC => "HEVC",
        VideoCodec.AV1 => "AV1",
        VideoCodec.Xvid => "XviD",
        VideoCodec.MPEG2 => "MPEG-2",
        VideoCodec.DNxHR => "DNxHR",
        VideoCodec.JPEG2000 => "J2K",
        VideoCodec.Uncompressed => "v210",
        _ => c.ToString(),
    };

    public static string Display(this EncoderBackend b) => b switch
    {
        EncoderBackend.Software => L.T("CPU (software)"),
        EncoderBackend.Nvenc => L.T("NVIDIA NVENC"),
        EncoderBackend.Qsv => L.T("Intel Quick Sync"),
        EncoderBackend.Amf => L.T("AMD AMF"),
        EncoderBackend.VideoToolbox => L.T("Apple VideoToolbox"),
        EncoderBackend.Vaapi => L.T("VA-API (Linux)"),
        EncoderBackend.Vulkan => L.T("Vulkan (any GPU)"),
        _ => b.ToString(),
    };

    public static string Short(this EncoderBackend b) => b switch
    {
        EncoderBackend.Software => "CPU",
        EncoderBackend.Nvenc => "NVENC",
        EncoderBackend.Qsv => "QSV",
        EncoderBackend.Amf => "AMF",
        EncoderBackend.VideoToolbox => "VT",
        EncoderBackend.Vaapi => "VAAPI",
        EncoderBackend.Vulkan => "VK",
        _ => b.ToString(),
    };

    public static string Display(this QualityLevel q) => q switch
    {
        QualityLevel.VeryHigh => L.T("Very high"),
        QualityLevel.High => L.T("High"),
        QualityLevel.AboveNormal => L.T("Above normal"),
        QualityLevel.Normal => L.T("Normal"),
        QualityLevel.BelowNormal => L.T("Below normal"),
        QualityLevel.Low => L.T("Low"),
        QualityLevel.VeryLow => L.T("Very low"),
        QualityLevel.Draft => L.T("Draft (test only)"),
        _ => q.ToString(),
    };

    public static string Display(this SubtitleMode m) => m switch
    {
        SubtitleMode.Hardsub => L.T("Hardsub (burn in)"),
        SubtitleMode.Softsub => L.T("Softsub (keep tracks)"),
        SubtitleMode.None => L.T("No subtitles"),
        _ => m.ToString(),
    };

    public static string Display(this HdrMode m) => m switch
    {
        HdrMode.Auto => L.T("Automatic (keep HDR when possible)"),
        HdrMode.Preserve => L.T("Keep HDR"),
        HdrMode.ToneMapSdr => L.T("Convert to SDR (tone mapping)"),
        _ => m.ToString(),
    };

    public static string Display(this FilterStrength f) => f switch
    {
        FilterStrength.Off => L.T("Off"),
        FilterStrength.Light => L.T("Light"),
        FilterStrength.Medium => L.T("Medium"),
        FilterStrength.Strong => L.T("Strong"),
        _ => f.ToString(),
    };

    public static string Display(this DeinterlaceMode d) => d switch
    {
        DeinterlaceMode.Auto => L.T("Automatic"),
        DeinterlaceMode.Off => L.T("Off"),
        DeinterlaceMode.Force => L.T("Always"),
        _ => d.ToString(),
    };

    public static string Display(this ImageMode m) => m switch
    {
        ImageMode.Interval => L.T("One every N seconds"),
        ImageMode.Count => L.T("N images spread over the video"),
        ImageMode.SceneChanges => L.T("At scene changes"),
        ImageMode.EveryFrame => L.T("Every frame"),
        _ => m.ToString(),
    };

    public static string Display(this PostQueueAction a) => a switch
    {
        PostQueueAction.Nothing => L.T("Do nothing"),
        PostQueueAction.CloseApp => L.T("Close KnA"),
        PostQueueAction.Sleep => L.T("Sleep"),
        PostQueueAction.Shutdown => L.T("Shut down"),
        _ => a.ToString(),
    };
}
