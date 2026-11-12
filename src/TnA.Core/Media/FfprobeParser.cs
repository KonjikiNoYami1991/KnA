using System.Globalization;
using System.Text.Json;

namespace TnA.Core.Media;

/// <summary>Turns <c>ffprobe -print_format json -show_format -show_streams</c> output into a <see cref="MediaInfo"/>.</summary>
public static class FfprobeParser
{
    public static MediaInfo Parse(string path, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var video = new List<VideoStream>();
        var audio = new List<AudioStream>();
        var subs = new List<SubtitleStream>();
        var attachments = new List<AttachmentStream>();

        TimeSpan duration = TimeSpan.Zero;
        long size = 0;
        string container = "";

        if (root.TryGetProperty("format", out var format))
        {
            duration = ParseSeconds(Str(format, "duration"));
            long.TryParse(Str(format, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            container = Str(format, "format_name") ?? "";
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                int index = Int(s, "index");
                string codec = Str(s, "codec_name") ?? "";
                string? profile = Str(s, "profile");
                var (isDefault, isForced, isAttachedPic) = Disposition(s);
                string? lang = Tag(s, "language");
                string? title = Tag(s, "title");

                switch (type)
                {
                    case "video":
                        var frameRateRational = Str(s, "avg_frame_rate") is { } avg && avg != "0/0" ? avg : Str(s, "r_frame_rate");
                        double fps = ParseRational(frameRateRational);
                        long frames = Long(s, "nb_frames");
                        if (frames <= 0 && Tag(s, "NUMBER_OF_FRAMES") is { } nf) long.TryParse(nf, out frames);
                        var streamDuration = ParseSeconds(Str(s, "duration"));
                        if (streamDuration == TimeSpan.Zero && Tag(s, "DURATION") is { } d) streamDuration = ParseClock(d);
                        if (frames <= 0 && fps > 0)
                        {
                            var dur = streamDuration > TimeSpan.Zero ? streamDuration : duration;
                            frames = (long)Math.Round(dur.TotalSeconds * fps);
                        }
                        string? pixFmt = Str(s, "pix_fmt");
                        video.Add(new VideoStream
                        {
                            Index = index, CodecName = codec, Profile = profile, Language = lang, Title = title,
                            IsDefault = isDefault, IsForced = isForced, IsAttachedPicture = isAttachedPic,
                            Width = Int(s, "width"), Height = Int(s, "height"),
                            SampleAspectRatio = ParseRatio(Str(s, "sample_aspect_ratio")),
                            FrameRate = fps, FrameRateRational = frameRateRational, FrameCount = frames,
                            PixelFormat = pixFmt, BitDepth = GuessBitDepth(pixFmt, Str(s, "bits_per_raw_sample")),
                            FieldOrder = Str(s, "field_order"),
                        });
                        if (duration == TimeSpan.Zero) duration = streamDuration;
                        break;
                    case "audio":
                        audio.Add(new AudioStream
                        {
                            Index = index, CodecName = codec, Profile = profile, Language = lang, Title = title,
                            IsDefault = isDefault, IsForced = isForced,
                            Channels = Int(s, "channels"), ChannelLayout = Str(s, "channel_layout"),
                            SampleRate = Int(s, "sample_rate"),
                        });
                        break;
                    case "subtitle":
                        subs.Add(new SubtitleStream
                        {
                            Index = index, CodecName = codec, Profile = profile, Language = lang, Title = title,
                            IsDefault = isDefault, IsForced = isForced,
                        });
                        break;
                    case "attachment":
                        attachments.Add(new AttachmentStream
                        {
                            Index = index, CodecName = codec, Language = lang, Title = title,
                            FileName = Tag(s, "filename"), MimeType = Tag(s, "mimetype"),
                        });
                        break;
                }
            }
        }

        return new MediaInfo
        {
            Path = path, ContainerFormat = container, Duration = duration, SizeBytes = size,
            Video = video, Audio = audio, Subtitles = subs, Attachments = attachments,
        };
    }

    private static int GuessBitDepth(string? pixFmt, string? bitsPerRawSample)
    {
        if (int.TryParse(bitsPerRawSample, out var bits) && bits > 0) return bits;
        if (pixFmt is null) return 8;
        if (pixFmt.Contains("p10") || pixFmt.Contains("p010")) return 10;
        if (pixFmt.Contains("p12")) return 12;
        if (pixFmt.Contains("p16")) return 16;
        return 8;
    }

    private static (bool isDefault, bool isForced, bool attachedPic) Disposition(JsonElement s)
    {
        if (!s.TryGetProperty("disposition", out var d)) return (false, false, false);
        return (Int(d, "default") == 1, Int(d, "forced") == 1, Int(d, "attached_pic") == 1);
    }

    private static string? Tag(JsonElement s, string name)
    {
        if (!s.TryGetProperty("tags", out var tags)) return null;
        foreach (var p in tags.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || p.Name.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int Int(JsonElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    private static long Long(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    internal static double ParseRational(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d == 0 ? 0 : n / d;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    /// <summary>Parses "16:15" style ratios; returns 1.0 when unknown.</summary>
    internal static double ParseRatio(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0:1" || value == "N/A") return 1.0;
        var parts = value.Split(':');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && n > 0 && d > 0)
            return n / d;
        return 1.0;
    }

    private static TimeSpan ParseSeconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
            ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;

    /// <summary>Parses Matroska "00:23:40.003000000" duration tags.</summary>
    private static TimeSpan ParseClock(string value)
    {
        var p = value.Split(':');
        if (p.Length == 3
            && int.TryParse(p[0], out var h) && int.TryParse(p[1], out var m)
            && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
        return TimeSpan.Zero;
    }
}
