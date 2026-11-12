using TnA.Core.Localization;

namespace TnA.Core.Encoding;

public enum VideoCodec { H264, HEVC, AV1, Xvid }

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

public static class EnumText
{
    public static string Display(this VideoCodec c) => c switch
    {
        VideoCodec.H264 => L.T("H.264 / AVC"),
        VideoCodec.HEVC => L.T("H.265 / HEVC"),
        VideoCodec.AV1 => L.T("AV1"),
        VideoCodec.Xvid => L.T("MPEG-4 ASP (XviD)"),
        _ => c.ToString(),
    };

    public static string Short(this VideoCodec c) => c switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.HEVC => "HEVC",
        VideoCodec.AV1 => "AV1",
        VideoCodec.Xvid => "XviD",
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
        PostQueueAction.CloseApp => L.T("Close TnA"),
        PostQueueAction.Sleep => L.T("Sleep"),
        PostQueueAction.Shutdown => L.T("Shut down"),
        _ => a.ToString(),
    };
}
