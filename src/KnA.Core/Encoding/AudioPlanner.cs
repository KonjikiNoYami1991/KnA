using System.Globalization;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>
/// The audio encoders a given FFmpeg build offers, and the best one for each codec.
/// AAC: libfdk_aac (custom builds) → aac_at (macOS AudioToolbox) → external fdkaac program → FFmpeg's own aac.
/// </summary>
public sealed record AudioEncoderSet(IReadOnlyCollection<string> Compiled, bool ExternalFdkaac = false)
{
    /// <summary>What a typical GPL FFmpeg build has (used before detection or in tests).</summary>
    public static AudioEncoderSet Default { get; } = new(
        ["aac", "libopus", "opus", "libmp3lame", "libvorbis", "vorbis", "ac3", "eac3", "flac", "alac", "wavpack", "truehd", "dca",
         "pcm_s16le", "pcm_s24le", "pcm_f32le", "pcm_s16be", "pcm_s24be"]);

    /// <summary>Pseudo encoder name: AAC made by the standalone fdkaac program (ffmpeg → WAV pipe → fdkaac).</summary>
    public const string Fdkaac = "fdkaac";

    public string? Best(AudioCodec c) => c switch
    {
        AudioCodec.Aac => First("libfdk_aac", "aac_at") ?? (ExternalFdkaac ? Fdkaac : null) ?? First("aac"),
        AudioCodec.HeAac => First("libfdk_aac", "aac_at") ?? (ExternalFdkaac ? Fdkaac : null),
        AudioCodec.Opus => First("libopus", "opus"),
        AudioCodec.Mp3 => First("libmp3lame"),
        AudioCodec.Vorbis => First("libvorbis", "vorbis"),
        AudioCodec.Ac3 => First("ac3", "ac3_fixed"),
        AudioCodec.Eac3 => First("eac3"),
        AudioCodec.Flac => First("flac"),
        AudioCodec.Alac => First("alac", "alac_at"),
        AudioCodec.WavPack => First("wavpack"),
        AudioCodec.TrueHd => First("truehd"),
        AudioCodec.Dts => First("dca"),
        AudioCodec.Pcm16 => First("pcm_s16le"),
        AudioCodec.Pcm24 => First("pcm_s24le"),
        AudioCodec.Pcm32Float => First("pcm_f32le"),
        _ => null,
    };

    private string? First(params string[] names) => names.FirstOrDefault(Compiled.Contains);

    /// <summary>Codec name of the stream an encoder produces (for container checks).</summary>
    public static string OutputCodec(string encoder) => encoder switch
    {
        "libfdk_aac" or "aac_at" or Fdkaac or "aac" => "aac",
        "libopus" or "opus" => "opus",
        "libmp3lame" => "mp3",
        "libvorbis" or "vorbis" => "vorbis",
        "ac3_fixed" => "ac3",
        "alac_at" => "alac",
        "dca" => "dts",
        _ => encoder,
    };

    /// <summary>Encoders that need "-strict experimental".</summary>
    public static bool IsExperimental(string encoder) => encoder is "opus" or "vorbis" or "truehd" or "dca";
}

/// <summary>What happens to one audio track.</summary>
public sealed record AudioTrackPlan
{
    /// <summary>FFmpeg encoder, <see cref="AudioEncoderSet.Fdkaac"/>, or null = stream copy.</summary>
    public string? Encoder { get; init; }
    public int Channels { get; init; }
    public int? Kbps { get; init; }
    public int? VbrQuality { get; init; }
    public bool HeAac { get; init; }
    public ValidationIssue? Issue { get; init; }

    public bool IsCopy => Encoder is null;
    public bool IsExternal => Encoder == AudioEncoderSet.Fdkaac;
}

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

    /// <summary>Backwards compatible overload: automatic codec, default encoders.</summary>
    public static List<string> BuildArgs(AudioStream a, int outIndex, ProfileKind profile, QualityLevel quality,
        string container, AudioFilterSettings filters) =>
        BuildArgs(Plan(a, new JobSettings { Profile = profile, Quality = quality, AudioFilters = filters }, container, AudioEncoderSet.Default),
            outIndex, filters);

    /// <summary>The job settings with the track's own codec/bitrate choice (track list) applied.</summary>
    public static JobSettings ForTrack(TrackPlan t, JobSettings s)
    {
        if (t.Output is { Length: > 0 } o && Enum.TryParse<AudioCodec>(o, true, out var codec) && Enum.IsDefined(codec))
            s = s with { AudioCodec = codec };
        if (t.Bitrate is > 0 and var k) s = s with { AudioBitrate = k };
        return s;
    }

    public static AudioTrackPlan Plan(AudioStream a, JobSettings s, string container, AudioEncoderSet encoders)
    {
        int q = (int)s.Quality;
        int ch = Math.Max(1, a.Channels);
        var filters = s.AudioFilters;
        if (filters.DownmixStereo) ch = Math.Min(ch, 2);
        string codec = a.CodecName.ToLowerInvariant();
        bool mustEncode = filters.IsActive;
        string aac = encoders.Best(AudioCodec.Aac) ?? "aac";

        int Scaled(int perPair, int channels) => perPair * Math.Max(2, channels) / 2;
        int? userKbps(int channels) => s.AudioBitrate is > 0 and var k ? Scaled(k, channels) : null;
        int AacKbps(int channels) => userKbps(channels) ?? Pick(q, 192, 160, 144, 128, 96, 64, 48, 32) * channels / 2;

        if (s.AudioCodec != AudioCodec.Auto)
            return Explicit(a, s, container, encoders, ch, codec, mustEncode, q, Scaled);

        // 1. What the profile wants (copy when possible).
        AudioTrackPlan target = s.Profile switch
        {
            ProfileKind.BlurayAac => codec == "aac" && !mustEncode ? Copy(ch)
                : Enc(aac, Math.Min(ch, 6), AacKbps(Math.Min(ch, 6))),
            ProfileKind.BlurayAc3 => codec == "ac3" && !mustEncode ? Copy(ch)
                : Enc("ac3", Math.Min(ch, 6), userKbps(Math.Min(ch, 6)) ?? Math.Min(640, Pick(q, 96, 96, 80, 80, 80, 64, 48, 32) * Math.Min(ch, 6))),
            ProfileKind.Streaming => codec == "aac" && ch <= 2 && !mustEncode && container != "webm" ? Copy(ch)
                : container == "webm" ? Enc(encoders.Best(AudioCodec.Opus) ?? "libopus", Math.Min(ch, 2), userKbps(2) ?? Pick(q, 160, 128, 112, 96, 80, 64, 48, 32))
                : Enc(aac, Math.Min(ch, 2), AacKbps(Math.Min(ch, 2))),
            ProfileKind.XvidMp3 => codec == "mp3" && ch <= 2 && !mustEncode ? Copy(ch)
                : Enc("libmp3lame", Math.Min(ch, 2), Math.Min(320, userKbps(2) ?? Pick(q, 160, 128, 112, 96, 80, 64, 48, 32) * Math.Min(ch, 2))),
            ProfileKind.Workraw => codec == "vorbis" && ch == 1 && !mustEncode ? Copy(1)
                : new AudioTrackPlan { Encoder = encoders.Best(AudioCodec.Vorbis) ?? "libvorbis", Channels = 1, VbrQuality = Pick(q, 7, 6, 5, 4, 3, 2, 1, 0) },
            ProfileKind.Professional => (a.IsLossless || codec.StartsWith("pcm_", StringComparison.Ordinal)) && !mustEncode && Containers.AcceptsAudio(container, codec)
                ? Copy(ch)
                : Enc(container == "mkv" ? "flac" : "pcm_s24le", ch, null),
            _ => mustEncode ? Enc(Fallback(container, a.IsLossless, encoders), ch, null) : Copy(ch),
        };

        // 2. The container must accept it: otherwise convert to the container's natural codec.
        if (target.IsCopy && !Containers.AcceptsAudio(container, codec))
            target = Enc(Fallback(container, a.IsLossless, encoders), ch, null);
        return WithDefaultBitrate(target, q, s.AudioBitrate);
    }

    private static AudioTrackPlan Explicit(AudioStream a, JobSettings s, string container, AudioEncoderSet encoders,
        int ch, string sourceCodec, bool mustEncode, int q, Func<int, int, int> scaled)
    {
        if (s.AudioCodec == AudioCodec.Copy)
        {
            if (!mustEncode && Containers.AcceptsAudio(container, sourceCodec)) return Copy(ch);
            var fb = Enc(Fallback(container, a.IsLossless, encoders), ch, null) with
            {
                Issue = mustEncode ? null : Containers.Warning("{0} audio cannot be copied into {1}: converted.", sourceCodec, container.ToUpperInvariant()),
            };
            return WithDefaultBitrate(fb, q, s.AudioBitrate);
        }

        var encoder = encoders.Best(s.AudioCodec);
        ValidationIssue? issue = null;
        bool he = s.AudioCodec == AudioCodec.HeAac;
        if (encoder is null)
        {
            if (he && encoders.Best(AudioCodec.Aac) is { } lc)
            {
                encoder = lc;
                he = false;
                issue = Containers.Warning("HE-AAC needs libfdk_aac, AudioToolbox or the fdkaac program: AAC-LC is used.");
            }
            else
            {
                return Copy(ch) with { Issue = Containers.Error("No encoder for {0} in this FFmpeg build.", s.AudioCodec.Display()) };
            }
        }

        var outCodec = AudioEncoderSet.OutputCodec(encoder);
        if (!Containers.AcceptsAudio(container, outCodec))
            return Copy(ch) with { Issue = Containers.Error("{0} audio cannot be stored in {1}. Choose another container or audio codec.", s.AudioCodec.Display(), container.ToUpperInvariant()) };

        // Same lossy codec, nothing to change: copying avoids a generation loss.
        if (!mustEncode && s.AudioBitrate is null && outCodec == sourceCodec && s.AudioCodec != AudioCodec.HeAac)
            return Copy(ch);

        int channels = s.AudioCodec switch
        {
            AudioCodec.Mp3 or AudioCodec.HeAac => Math.Min(ch, 2),
            AudioCodec.Ac3 or AudioCodec.Eac3 or AudioCodec.Dts => Math.Min(ch, 6),
            _ => ch,
        };
        if (s.AudioCodec == AudioCodec.HeAac && ch > 2)
            issue ??= Containers.Warning("HE-AAC is limited to stereo here: the audio is downmixed.");
        int? kbps = s.AudioCodec switch
        {
            AudioCodec.Aac => s.AudioBitrate is > 0 ? scaled(s.AudioBitrate.Value, channels) : Pick(q, 192, 160, 144, 128, 96, 64, 48, 32) * Math.Max(2, channels) / 2,
            AudioCodec.HeAac when he => s.AudioBitrate is > 0 ? s.AudioBitrate : Pick(q, 80, 64, 56, 48, 40, 32, 24, 24),
            AudioCodec.HeAac => Pick(q, 192, 160, 144, 128, 96, 64, 48, 32) * Math.Max(2, channels) / 2,
            AudioCodec.Opus => s.AudioBitrate is > 0 ? scaled(s.AudioBitrate.Value, channels) : Pick(q, 160, 128, 112, 96, 80, 64, 48, 32) * Math.Max(2, channels) / 2,
            AudioCodec.Mp3 => Math.Min(320, s.AudioBitrate ?? Pick(q, 320, 256, 224, 192, 160, 128, 96, 64)),
            AudioCodec.Ac3 => Math.Min(640, s.AudioBitrate is > 0 ? scaled(s.AudioBitrate.Value, channels) : Pick(q, 224, 192, 192, 160, 160, 128, 96, 64) * Math.Max(2, channels) / 2),
            AudioCodec.Eac3 => Math.Min(6144, s.AudioBitrate is > 0 ? scaled(s.AudioBitrate.Value, channels) : Pick(q, 256, 224, 192, 192, 160, 128, 96, 64) * Math.Max(2, channels) / 2),
            AudioCodec.Dts => channels > 2 ? 1536 : 768,
            _ => null,
        };
        int? vbr = s.AudioCodec == AudioCodec.Vorbis && s.AudioBitrate is null ? Pick(q, 8, 7, 6, 5, 4, 3, 2, 1) : null;
        if (vbr is not null) kbps = null;
        else if (s.AudioCodec == AudioCodec.Vorbis) kbps = scaled(s.AudioBitrate!.Value, channels);
        return new AudioTrackPlan
        {
            Encoder = encoder, Channels = channels, Kbps = kbps, VbrQuality = vbr, Issue = issue,
            HeAac = he,
        };
    }

    /// <summary>Builds "-c:a:N …" options for output audio stream number <paramref name="outIndex"/>.</summary>
    public static List<string> BuildArgs(AudioTrackPlan plan, int outIndex, AudioFilterSettings filters)
    {
        string n = outIndex.ToString(CultureInfo.InvariantCulture);
        if (plan.IsCopy) return [$"-c:a:{n}", "copy"];
        // Without a pre-encoded fdkaac file (validation, previews) FFmpeg's encoder stands in.
        var encoder = plan.IsExternal ? "aac" : plan.Encoder!;
        var l = new List<string> { $"-c:a:{n}", encoder, $"-ac:a:{n}", plan.Channels.ToString(CultureInfo.InvariantCulture) };
        if (plan.Kbps is { } k) l.AddRange([$"-b:a:{n}", $"{k}k"]);
        if (plan.VbrQuality is { } vq) l.AddRange([$"-q:a:{n}", vq.ToString(CultureInfo.InvariantCulture)]);
        switch (encoder)
        {
            case "libfdk_aac" when plan.HeAac: l.AddRange([$"-profile:a:{n}", "aac_he"]); break;
            case "aac_at" when plan.HeAac: l.AddRange([$"-profile:a:{n}", "aac_he"]); break;
            case "aac_at": l.AddRange([$"-aac_at_quality:a:{n}", "0"]); break;
            case "flac": l.AddRange([$"-compression_level:a:{n}", "8"]); break;
        }
        if (AudioEncoderSet.IsExperimental(encoder)) l.AddRange([$"-strict:a:{n}", "experimental"]);
        if (FilterBuilder.Audio(filters, encoder, plan.Channels) is { } af) l.AddRange([$"-filter:a:{n}", af]);
        return l;
    }

    /// <summary>fdkaac arguments (reads a WAV from stdin) for an external AAC track.</summary>
    public static List<string> FdkaacArgs(AudioTrackPlan plan, string output)
    {
        var l = new List<string> { "--ignorelength", "-S", "-p", plan.HeAac ? "5" : "2" };
        if (plan.Kbps is { } k) l.AddRange(["-m", "0", "-b", (k * 1000).ToString(CultureInfo.InvariantCulture)]);
        else l.AddRange(["-m", "4"]);
        l.AddRange(["-o", output, "-"]);
        return l;
    }

    private static string Fallback(string container, bool lossless, AudioEncoderSet encoders)
    {
        var name = Containers.FallbackAudioEncoder(container, lossless);
        return name == "aac" ? encoders.Best(AudioCodec.Aac) ?? "aac" : name;
    }

    private static AudioTrackPlan Copy(int ch) => new() { Encoder = null, Channels = ch };
    private static AudioTrackPlan Enc(string encoder, int ch, int? kbps) => new() { Encoder = encoder, Channels = ch, Kbps = kbps };

    private static AudioTrackPlan WithDefaultBitrate(AudioTrackPlan p, int q, int? userKbps)
    {
        if (p.IsCopy || p.Kbps is not null || p.VbrQuality is not null) return p;
        int? Scaled(int perPair) => (userKbps is > 0 ? userKbps.Value : perPair) * Math.Max(2, p.Channels) / 2;
        int? kbps = AudioEncoderSet.OutputCodec(p.Encoder!) switch
        {
            "aac" => Scaled(Pick(q, 192, 160, 144, 128, 96, 64, 48, 32)),
            "opus" => Scaled(Pick(q, 128, 112, 96, 96, 80, 64, 48, 32)),
            "mp3" => Math.Min(320, Scaled(Pick(q, 160, 128, 112, 96, 80, 64, 48, 32))!.Value),
            "ac3" => Math.Min(640, 96 * p.Channels),
            _ => null, // flac / alac / pcm: lossless
        };
        return p with { Kbps = kbps };
    }

    private static int Pick(int index, params int[] values) => values[Math.Clamp(index, 0, values.Length - 1)];
}
