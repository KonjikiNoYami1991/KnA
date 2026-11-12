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
}

public enum PostQueueAction { Nothing, CloseApp, Sleep, Shutdown }

public static class EnumText
{
    public static string Display(this VideoCodec c) => c switch
    {
        VideoCodec.H264 => "H.264 / AVC",
        VideoCodec.HEVC => "H.265 / HEVC",
        VideoCodec.AV1 => "AV1",
        VideoCodec.Xvid => "MPEG-4 ASP (XviD)",
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
        EncoderBackend.Software => "CPU (software)",
        EncoderBackend.Nvenc => "NVIDIA NVENC",
        EncoderBackend.Qsv => "Intel Quick Sync",
        EncoderBackend.Amf => "AMD AMF",
        EncoderBackend.VideoToolbox => "Apple VideoToolbox",
        EncoderBackend.Vaapi => "VA-API (Linux)",
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
        QualityLevel.VeryHigh => "Very high",
        QualityLevel.High => "High",
        QualityLevel.AboveNormal => "Above normal",
        QualityLevel.Normal => "Normal",
        QualityLevel.BelowNormal => "Below normal",
        QualityLevel.Low => "Low",
        QualityLevel.VeryLow => "Very low",
        QualityLevel.Draft => "Draft (test only)",
        _ => q.ToString(),
    };

    public static string Display(this SubtitleMode m) => m switch
    {
        SubtitleMode.Hardsub => "Hardsub (burn in)",
        SubtitleMode.Softsub => "Softsub (keep tracks)",
        SubtitleMode.None => "No subtitles",
        _ => m.ToString(),
    };

    public static string Display(this PostQueueAction a) => a switch
    {
        PostQueueAction.Nothing => "Do nothing",
        PostQueueAction.CloseApp => "Close TnA",
        PostQueueAction.Sleep => "Sleep",
        PostQueueAction.Shutdown => "Shut down",
        _ => a.ToString(),
    };
}
