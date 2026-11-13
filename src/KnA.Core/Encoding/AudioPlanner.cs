using System.Globalization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

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

    /// <summary>Backwards compatible overload (container = profile default, no filters).</summary>
    public static List<string> BuildArgs(AudioStream a, int outIndex, ProfileKind profile, QualityLevel quality) =>
        BuildArgs(a, outIndex, profile, quality, Profiles.Get(profile).Container, new AudioFilterSettings());

    /// <summary>Builds "-c:a:N …" options for output audio stream number <paramref name="outIndex"/>.</summary>
    public static List<string> BuildArgs(AudioStream a, int outIndex, ProfileKind profile, QualityLevel quality,
        string container, AudioFilterSettings filters)
    {
        string n = outIndex.ToString(CultureInfo.InvariantCulture);
        int q = (int)quality;
        int ch = Math.Max(1, a.Channels);
        if (filters.DownmixStereo) ch = Math.Min(ch, 2);
        string codec = a.CodecName.ToLowerInvariant();
        bool mustEncode = filters.IsActive;

        int AacKbps(int channels) => Pick(q, 192, 160, 144, 128, 96, 64, 48, 32) * channels / 2;

        // 1. What the profile wants (copy when possible).
        (string? encoder, int channels, int? kbps, int? vbrQ) target = profile switch
        {
            ProfileKind.BlurayAac => codec == "aac" && !mustEncode ? (null, ch, null, null) : ("aac", Math.Min(ch, 6), AacKbps(Math.Min(ch, 6)), null),
            ProfileKind.BlurayAc3 => codec == "ac3" && !mustEncode ? (null, ch, null, null)
                : ("ac3", Math.Min(ch, 6), Math.Min(640, Pick(q, 96, 96, 80, 80, 80, 64, 48, 32) * Math.Min(ch, 6)), null),
            ProfileKind.Streaming => codec == "aac" && ch <= 2 && !mustEncode && container != "webm" ? (null, ch, null, null)
                : container == "webm" ? ("libopus", Math.Min(ch, 2), Pick(q, 160, 128, 112, 96, 80, 64, 48, 32), null)
                : ("aac", Math.Min(ch, 2), AacKbps(Math.Min(ch, 2)), null),
            ProfileKind.XvidMp3 => codec == "mp3" && ch <= 2 && !mustEncode ? (null, ch, null, null)
                : ("libmp3lame", Math.Min(ch, 2), Pick(q, 160, 128, 112, 96, 80, 64, 48, 32) * Math.Min(ch, 2), null),
            ProfileKind.Workraw => codec == "vorbis" && ch == 1 && !mustEncode ? (null, 1, null, null)
                : ("libvorbis", 1, null, Pick(q, 7, 6, 5, 4, 3, 2, 1, 0)),
            _ => mustEncode ? (Containers.FallbackAudioEncoder(container, a.IsLossless), ch, null, null) : (null, ch, null, null),
        };

        // 2. The container must accept it: otherwise convert to the container's natural codec.
        if (target.encoder is null && !Containers.AcceptsAudio(container, codec))
            target = (Containers.FallbackAudioEncoder(container, a.IsLossless), ch, null, null);

        if (target.encoder is null) return [$"-c:a:{n}", "copy"];

        var l = new List<string> { $"-c:a:{n}", target.encoder, $"-ac:a:{n}", target.channels.ToString(CultureInfo.InvariantCulture) };
        int? kbps = target.kbps ?? target.encoder switch
        {
            "aac" => AacKbps(target.channels),
            "libopus" => Pick(q, 128, 112, 96, 96, 80, 64, 48, 32) * Math.Max(2, target.channels) / 2,
            "libmp3lame" => Pick(q, 160, 128, 112, 96, 80, 64, 48, 32) * Math.Min(target.channels, 2),
            "ac3" => Math.Min(640, 96 * target.channels),
            _ => null, // flac / alac: lossless
        };
        if (kbps is { } k) l.AddRange([$"-b:a:{n}", $"{k}k"]);
        if (target.vbrQ is { } vq) l.AddRange([$"-q:a:{n}", vq.ToString(CultureInfo.InvariantCulture)]);
        if (FilterBuilder.Audio(filters, target.encoder, target.channels) is { } af) l.AddRange([$"-filter:a:{n}", af]);
        return l;
    }

    private static int Pick(int index, params int[] values) => values[Math.Clamp(index, 0, values.Length - 1)];
}
