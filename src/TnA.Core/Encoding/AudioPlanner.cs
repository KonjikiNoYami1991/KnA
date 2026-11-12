using System.Globalization;
using TnA.Core.Media;

namespace TnA.Core.Encoding;

/// <summary>Decides, per audio track, whether to copy it or how to re-encode it.</summary>
public static class AudioPlanner
{
    public static IReadOnlyList<AudioStream> SelectTracks(MediaInfo media, SubtitleMode mode, ProfileKind profile)
    {
        if (media.Audio.Count == 0) return [];
        // Hardsub output (and the legacy players it targets) keeps a single track: forced → default → first.
        bool singleTrack = mode == SubtitleMode.Hardsub || profile is ProfileKind.XvidMp3 or ProfileKind.Workraw;
        if (!singleTrack) return media.Audio;
        var pick = media.Audio.FirstOrDefault(a => a.IsForced)
                   ?? media.Audio.FirstOrDefault(a => a.IsDefault)
                   ?? media.Audio[0];
        return [pick];
    }

    /// <summary>Builds "-c:a:N ..." options for output audio stream number <paramref name="outIndex"/>.</summary>
    public static List<string> BuildArgs(AudioStream a, int outIndex, ProfileKind profile, QualityLevel quality)
    {
        string n = outIndex.ToString(CultureInfo.InvariantCulture);
        int q = (int)quality;
        int ch = Math.Max(1, a.Channels);
        string codec = a.CodecName.ToLowerInvariant();

        List<string> Copy() => [$"-c:a:{n}", "copy"];
        List<string> Encode(string enc, int channels, int? kbps = null, int? vbrQ = null)
        {
            var l = new List<string> { $"-c:a:{n}", enc, $"-ac:a:{n}", channels.ToString(CultureInfo.InvariantCulture) };
            if (kbps is { } k) l.AddRange([$"-b:a:{n}", $"{k}k"]);
            if (vbrQ is { } v) l.AddRange([$"-q:a:{n}", v.ToString(CultureInfo.InvariantCulture)]);
            return l;
        }

        int AacKbps(int channels) => Pick(q, 192, 160, 144, 128, 96, 64, 48, 32) * channels / 2;

        switch (profile)
        {
            case ProfileKind.BlurayAac:
                return codec == "aac" ? Copy() : Encode("aac", Math.Min(ch, 6), AacKbps(Math.Min(ch, 6)));
            case ProfileKind.BlurayAc3:
                {
                    if (codec == "ac3") return Copy();
                    int c = Math.Min(ch, 6);
                    return Encode("ac3", c, Math.Min(640, Pick(q, 96, 96, 80, 80, 80, 64, 48, 32) * c));
                }
            case ProfileKind.Streaming:
                return codec == "aac" && ch <= 2 ? Copy() : Encode("aac", Math.Min(ch, 2), AacKbps(Math.Min(ch, 2)));
            case ProfileKind.UniversalMkv:
                return Copy(); // Matroska takes any codec: never lose quality on audio.
            case ProfileKind.XvidMp3:
                {
                    if (codec == "mp3" && ch <= 2) return Copy();
                    int c = Math.Min(ch, 2);
                    return Encode("libmp3lame", c, Pick(q, 160, 128, 112, 96, 80, 64, 48, 32) * c);
                }
            case ProfileKind.Workraw:
                return codec == "vorbis" && ch == 1 ? Copy() : Encode("libvorbis", 1, vbrQ: Pick(q, 7, 6, 5, 4, 3, 2, 1, 0));
            case ProfileKind.RemuxMp4:
                if (codec is "aac" or "ac3" or "eac3" or "mp3" or "alac" or "opus" or "flac") return Copy();
                return a.IsLossless ? [$"-c:a:{n}", "alac"] : Encode("aac", Math.Min(ch, 6), AacKbps(Math.Min(ch, 6)));
            default:
                return Copy();
        }
    }

    private static int Pick(int index, params int[] values) => values[Math.Clamp(index, 0, values.Length - 1)];
}
