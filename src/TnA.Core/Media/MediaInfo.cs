using System.Globalization;

namespace TnA.Core.Media;

/// <summary>Everything TnA needs to know about a source file, as reported by ffprobe.</summary>
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
            }
            if (Duration > TimeSpan.Zero) parts.Add(Duration.ToString(Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));
            if (Audio.Count > 0) parts.Add($"{Audio.Count} audio");
            if (Subtitles.Count > 0) parts.Add($"{Subtitles.Count} subs");
            if (Attachments.Count > 0) parts.Add($"{Attachments.Count} fonts");
            return string.Join(" · ", parts);
        }
    }
}

public abstract class StreamBase
{
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
