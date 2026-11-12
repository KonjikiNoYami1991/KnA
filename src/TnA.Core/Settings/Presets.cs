using System.Text.Json;
using TnA.Core.Encoding;
using TnA.Core.Localization;
using TnA.Core.Tools;

namespace TnA.Core.Settings;

public sealed record Preset(string Name, string? Description, JobSettings Settings, bool BuiltIn = false);

/// <summary>Built-in presets plus the user's own (presets.json in the data folder).</summary>
public sealed class PresetStore(string dataDirectory)
{
    private readonly object _lock = new();
    private string FilePath => Path.Combine(dataDirectory, "presets.json");

    public static IReadOnlyList<Preset> BuiltIns => [
        new(L.T("Anime archive — HEVC 10-bit"), L.T("MKV, HEVC 10-bit on the CPU, subtitles and fonts kept."),
            new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, TenBit = true, Quality = QualityLevel.High, Resolution = 0, Subtitles = SubtitleMode.Softsub }, true),
        new(L.T("Anime archive — AV1 10-bit"), L.T("MKV, SVT-AV1 10-bit, smallest files, subtitles and fonts kept."),
            new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.AV1, TenBit = true, Quality = QualityLevel.AboveNormal, Resolution = 0, Subtitles = SubtitleMode.Softsub }, true),
        new(L.T("Hardsub 1080p — H.264"), L.T("MP4 with burned subtitles that plays everywhere."),
            new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Quality = QualityLevel.Normal, Resolution = 1080, Subtitles = SubtitleMode.Hardsub }, true),
        new(L.T("Hardsub 720p — phones"), L.T("Small MP4 with burned subtitles, stereo audio."),
            new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Quality = QualityLevel.BelowNormal, Resolution = 720, Subtitles = SubtitleMode.Hardsub, AudioFilters = new() { DownmixStereo = true } }, true),
        new(L.T("TV / Blu-ray player"), L.T("H.264 High@4.1 with AAC, burned subtitles."),
            new JobSettings { Profile = ProfileKind.BlurayAac, Quality = QualityLevel.High, Resolution = 1080, Subtitles = SubtitleMode.Hardsub }, true),
        new(L.T("HDR → SDR"), L.T("Converts HDR/Dolby Vision to SDR H.264 for any screen."),
            new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Hdr = HdrMode.ToneMapSdr, Quality = QualityLevel.High, Resolution = 0, Subtitles = SubtitleMode.Softsub }, true),
        new(L.T("Workraw"), L.T("Fast-decoding raw for timing and typesetting."),
            new JobSettings { Profile = ProfileKind.Workraw, Quality = QualityLevel.Normal, Resolution = 720 }, true),
        new(L.T("Screenshots every 10 s (PNG)"), L.T("PNG images with subtitles, for checks and thumbnails."),
            new JobSettings { Profile = ProfileKind.Images, Resolution = 0, Subtitles = SubtitleMode.Hardsub, Images = new() { Format = ImageFormat.Png, Mode = ImageMode.Interval, IntervalSeconds = 10 } }, true),
        new(L.T("Extract all streams"), L.T("Demux every track, attachment and chapter."),
            new JobSettings { Profile = ProfileKind.Demux }, true),
    ];

    public IReadOnlyList<Preset> User
    {
        get
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(FilePath))
                        return JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(FilePath), SettingsStore.Json) ?? [];
                }
                catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { }
                return [];
            }
        }
    }

    public IReadOnlyList<Preset> All => [.. BuiltIns, .. User];

    public Preset? Get(string name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? BuiltInsAnyLanguage().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Preset Save(Preset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name)) throw new ArgumentException(L.T("The preset needs a name."));
        if (BuiltIns.Any(b => b.Name.Equals(preset.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L.T("A built-in preset already has this name."));
        var p = preset with { Name = preset.Name.Trim(), BuiltIn = false, Settings = preset.Settings with { PresetName = preset.Name.Trim() } };
        lock (_lock)
        {
            var list = User.Where(u => !u.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Append(p).OrderBy(u => u.Name).ToList();
            Write(list);
        }
        return p;
    }

    public bool Delete(string name)
    {
        lock (_lock)
        {
            var list = User.ToList();
            int n = list.RemoveAll(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Write(list);
            return n > 0;
        }
    }

    private void Write(List<Preset> list)
    {
        Directory.CreateDirectory(dataDirectory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, SettingsStore.Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    // Send-to shortcuts may have been created in another language.
    private static IEnumerable<Preset> BuiltInsAnyLanguage() => BuiltIns;
}

/// <summary>The questions of the preset wizard.</summary>
public enum WizardGoal { Archive, Tv, Web, Phone, OldPlayer, Editing, Images, Extract }
public enum WizardCodec { Compatible, Balanced, Smallest }
public enum WizardSpeed { BestQuality, Fast }
public enum WizardAudio { Keep, Stereo, Normalize }

public sealed record WizardAnswers(
    WizardGoal Goal,
    WizardCodec Codec = WizardCodec.Balanced,
    WizardSpeed Speed = WizardSpeed.BestQuality,
    QualityLevel Quality = QualityLevel.Normal,
    int Resolution = 0,
    SubtitleMode Subtitles = SubtitleMode.Hardsub,
    WizardAudio Audio = WizardAudio.Keep,
    HdrMode Hdr = HdrMode.Auto,
    VideoFilterSettings? Filters = null);

/// <summary>Turns plain-language answers into settings, choosing a working GPU when "fast" is asked.</summary>
public static class PresetWizard
{
    public static JobSettings Build(WizardAnswers a, FfmpegCapabilities? caps)
    {
        var profile = a.Goal switch
        {
            WizardGoal.Archive => ProfileKind.UniversalMkv,
            WizardGoal.Tv => ProfileKind.BlurayAac,
            WizardGoal.Web or WizardGoal.Phone => ProfileKind.Streaming,
            WizardGoal.OldPlayer => ProfileKind.XvidMp3,
            WizardGoal.Editing => ProfileKind.Workraw,
            WizardGoal.Images => ProfileKind.Images,
            _ => ProfileKind.Demux,
        };
        var codec = a.Codec switch
        {
            WizardCodec.Compatible => VideoCodec.H264,
            WizardCodec.Smallest => VideoCodec.AV1,
            _ => VideoCodec.HEVC,
        };
        if (a.Goal is WizardGoal.Phone or WizardGoal.Tv or WizardGoal.Editing) codec = VideoCodec.H264;
        var p = Profiles.Get(profile);
        if (!p.Codecs.Contains(codec)) codec = p.Codecs.FirstOrDefault(VideoCodec.H264);

        var backend = EncoderBackend.Software;
        if (a.Speed == WizardSpeed.Fast && p.AllowsHardware && caps is not null)
        {
            bool tenBit = codec != VideoCodec.H264;
            foreach (var b in new[] { EncoderBackend.Nvenc, EncoderBackend.Qsv, EncoderBackend.Amf, EncoderBackend.VideoToolbox, EncoderBackend.Vaapi })
                if (caps.IsUsable(codec, b, tenBit) || caps.IsUsable(codec, b, false)) { backend = b; break; }
        }

        int resolution = a.Goal == WizardGoal.Phone && a.Resolution is 0 or > 720 ? 720 : a.Resolution;
        return new JobSettings
        {
            Profile = profile,
            Codec = codec,
            Backend = backend,
            Quality = a.Quality,
            Resolution = resolution,
            Subtitles = profile == ProfileKind.Workraw ? SubtitleMode.None : a.Subtitles,
            TenBit = codec != VideoCodec.H264,
            Hdr = a.Hdr,
            VideoFilters = a.Filters ?? new(),
            AudioFilters = a.Audio switch
            {
                WizardAudio.Stereo => new AudioFilterSettings { DownmixStereo = true },
                WizardAudio.Normalize => new AudioFilterSettings { Normalize = true },
                _ => new AudioFilterSettings(),
            },
        };
    }
}
