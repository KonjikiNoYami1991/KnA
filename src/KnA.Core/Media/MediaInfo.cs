using System.Globalization;

namespace KnA.Core.Media;

/// <summary>Everything KnA needs to know about a source file, as reported by ffprobe.</summary>
public sealed class MediaInfo
{
    public required string Path { get; init; }
    public string ContainerFormat { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<VideoStream> Video { get; init; } = [];
    public IReadOnlyList<AudioStream> Audio { get; init; } = [];
    public IReadOnlyList<SubtitleStream> Subtitles { get; init; } = [];
    public IReadOnlyList<AttachmentStream> Attachments { get; init; } = [];
    /// <summary>Data/unknown streams (SCTE-35, teletext, bin_data, timed ID3…): never mapped to outputs.</summary>
    public IReadOnlyList<StreamBase> Ignored { get; init; } = [];
    public int ChapterCount { get; init; }
    /// <summary>Chapters of the container (seconds), in order.</summary>
    public IReadOnlyList<ChapterInfo> Chapters { get; init; } = [];

    /// <summary>MPEG transport streams and friends need extra care (unknown metadata, broken timestamps).</summary>
    public bool IsTransportStream =>
        ContainerFormat.Contains("mpegts", StringComparison.OrdinalIgnoreCase)
        || System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".ts" or ".m2ts" or ".mts" or ".tp" or ".trp" or ".vob" or ".mpg" or ".mpeg";

    /// <summary>Every stream in container order.</summary>
    public IEnumerable<StreamBase> AllStreams =>
        Video.Cast<StreamBase>().Concat(Audio).Concat(Subtitles).Concat(Attachments).OrderBy(s => s.Index);

    /// <summary>First real video stream (cover art / attached pictures are skipped).</summary>
    public VideoStream? PrimaryVideo => Video.FirstOrDefault(v => !v.IsAttachedPicture) ?? Video.FirstOrDefault();

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (PrimaryVideo is { } v)
            {
                parts.Add($"{v.Width}×{v.Height}");
                parts.Add(v.CodecName.ToUpperInvariant() + (v.BitDepth > 8 ? $" {v.BitDepth}-bit" : ""));
                if (v.FrameRate > 0) parts.Add(v.FrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps");
                if (v.IsInterlaced) parts.Add("interlaced");
                if (v.Rotation != 0) parts.Add($"↻{v.Rotation}°");
                if (v.Hdr.IsHdr) parts.Add(v.Hdr.Description);
            }
            if (Duration > TimeSpan.Zero) parts.Add(Duration.ToString(Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));
            if (Audio.Count > 0) parts.Add($"{Audio.Count} audio");
            if (Subtitles.Count > 0) parts.Add($"{Subtitles.Count} subs");
            if (Attachments.Count > 0) parts.Add($"{Attachments.Count} fonts");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record ChapterInfo(double Start, double End, string? Title);

public class StreamBase
{
    public string Kind { get; init; } = "";
    /// <summary>Absolute stream index inside the container (usable as <c>-map 0:{Index}</c>).</summary>
    public int Index { get; init; }
    public string CodecName { get; init; } = "";
    public string? Profile { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
}

public sealed class VideoStream : StreamBase
{
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Sample (pixel) aspect ratio, 1.0 for square pixels.</summary>
    public double SampleAspectRatio { get; init; } = 1.0;
    public double FrameRate { get; init; }
    /// <summary>Frame rate as an exact rational ("24000/1001"), when known.</summary>
    public string? FrameRateRational { get; init; }
    public long FrameCount { get; init; }
    public string? PixelFormat { get; init; }
    public int BitDepth { get; init; } = 8;
    public string? FieldOrder { get; init; }
    public bool IsAttachedPicture { get; init; }

    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorRange { get; init; }
    public HdrInfo Hdr { get; set; } = HdrInfo.Sdr;
    /// <summary>Clockwise rotation requested by the container (display matrix / "rotate" tag): 0, 90, 180, 270.</summary>
    public int Rotation { get; init; }

    public bool IsInterlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";
    public double DisplayWidth => Width * (SampleAspectRatio > 0 ? SampleAspectRatio : 1.0);
    public double DisplayAspectRatio => Height > 0 ? DisplayWidth / Height : 0;
}

public sealed class AudioStream : StreamBase
{
    public int Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int SampleRate { get; init; }

    private static readonly string[] LosslessCodecs = ["flac", "alac", "truehd", "mlp", "wavpack", "tta", "ape", "wmalossless"];

    public bool IsLossless =>
        CodecName.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
        || LosslessCodecs.Contains(CodecName, StringComparer.OrdinalIgnoreCase)
        || (CodecName.Equals("dts", StringComparison.OrdinalIgnoreCase) && (Profile?.Contains("MA", StringComparison.OrdinalIgnoreCase) ?? false));
}

public sealed class SubtitleStream : StreamBase
{
    private static readonly string[] TextCodecs = ["ass", "ssa", "subrip", "srt", "webvtt", "mov_text", "text"];
    private static readonly string[] BitmapCodecs = ["hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub"];

    public bool IsText => TextCodecs.Contains(CodecName, StringComparer.OrdinalIgnoreCase);
    public bool IsBitmap => BitmapCodecs.Contains(CodecName, StringComparer.OrdinalIgnoreCase);
    public bool IsAss => CodecName is "ass" or "ssa";
}

public sealed class AttachmentStream : StreamBase
{
    public string? FileName { get; init; }
    public string? MimeType { get; init; }

    public bool IsFont =>
        (MimeType?.Contains("font", StringComparison.OrdinalIgnoreCase) ?? false)
        || (MimeType?.Contains("truetype", StringComparison.OrdinalIgnoreCase) ?? false)
        || (MimeType?.Contains("opentype", StringComparison.OrdinalIgnoreCase) ?? false)
        || System.IO.Path.GetExtension(FileName ?? "").ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc";
}

public enum HdrFormat { Sdr, Hdr10, Hdr10Plus, Hlg, DolbyVision }

/// <summary>Static mastering display metadata (SMPTE ST 2086), chromaticities 0..1, luminance in cd/m².</summary>
public sealed record MasteringDisplay(
    double RedX, double RedY, double GreenX, double GreenY, double BlueX, double BlueY,
    double WhiteX, double WhiteY, double MinLuminance, double MaxLuminance)
{
    /// <summary>x265 "master-display" syntax (chromaticity in 0.00002 units, luminance in 0.0001 cd/m²).</summary>
    public string ToX265() =>
        $"G({C(GreenX)},{C(GreenY)})B({C(BlueX)},{C(BlueY)})R({C(RedX)},{C(RedY)})WP({C(WhiteX)},{C(WhiteY)})" +
        $"L({(long)Math.Round(MaxLuminance * 10000)},{(long)Math.Round(MinLuminance * 10000)})";

    private static long C(double v) => (long)Math.Round(v * 50000);
}

public sealed record HdrInfo
{
    public static readonly HdrInfo Sdr = new();

    public HdrFormat Format { get; init; } = HdrFormat.Sdr;
    /// <summary>Also true for HDR10 base layers carried along Dolby Vision / HDR10+.</summary>
    public bool HasHdr10Plus { get; init; }
    public int? DolbyVisionProfile { get; init; }
    /// <summary>Base-layer compatibility id: 1 = HDR10, 2 = SDR, 4 = HLG, 0 = none (profile 5).</summary>
    public int? DolbyVisionCompatibility { get; init; }
    public bool DolbyVisionHasEnhancementLayer { get; init; }
    public MasteringDisplay? Mastering { get; init; }
    public int? MaxCll { get; init; }
    public int? MaxFall { get; init; }

    public bool IsHdr => Format != HdrFormat.Sdr;
    public bool IsDolbyVision => DolbyVisionProfile is not null;
    /// <summary>Dolby Vision profile 5 has no HDR10/SDR fallback: without DV processing colors are wrong.</summary>
    public bool DolbyVisionWithoutFallback => IsDolbyVision && (DolbyVisionCompatibility ?? 0) == 0;
    public bool IsHlg => Format == HdrFormat.Hlg || DolbyVisionCompatibility == 4;

    public string Description => Format switch
    {
        HdrFormat.DolbyVision => $"Dolby Vision P{DolbyVisionProfile}" + (DolbyVisionCompatibility switch
        {
            1 => " (HDR10 base)", 2 => " (SDR base)", 4 => " (HLG base)", _ => "",
        }) + (HasHdr10Plus ? " + HDR10+" : ""),
        HdrFormat.Hdr10Plus => "HDR10+",
        HdrFormat.Hdr10 => "HDR10",
        HdrFormat.Hlg => "HLG",
        _ => "SDR",
    };
}
