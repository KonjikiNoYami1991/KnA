using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Queue;
using KnA.Core.Settings;
using Xunit;

namespace KnA.Core.Tests;

/// <summary>Audio and Subtitles as tab views: per-track audio filters, duplicated tracks, +/✕/← → operations.</summary>
public class TrackTabsTests
{
    private const string F = "/m/film.mkv";

    private static MediaInfo Media() => new()
    {
        Path = F, Duration = TimeSpan.FromMinutes(24),
        Video = [new VideoStream { Index = 0, Kind = "video", CodecName = "h264", Width = 1920, Height = 1080, FrameRate = 23.976 }],
        Audio = [new AudioStream { Index = 1, Kind = "audio", CodecName = "flac", Channels = 6, Language = "ita" },
                 new AudioStream { Index = 2, Kind = "audio", CodecName = "aac", Channels = 2, Language = "jpn" }],
        Subtitles = [new SubtitleStream { Index = 3, Kind = "subtitle", CodecName = "ass", Language = "ita" }],
    };

    private static CompileContext Ctx(JobSettings s, List<TrackPlan> tracks, Dictionary<string, string>? pre = null) => new()
    {
        Source = Media(), Media = new Dictionary<string, MediaInfo> { [F] = Media() }, Settings = s, Edit = new JobEdit { Tracks = tracks },
        Options = new BuildOptions { AudioEncoders = new AudioEncoderSet(["aac", "libopus", "libvorbis", "ac3", "flac"]) },
        PreEncodedAudio = pre ?? [],
    };

    private static readonly JobSettings Mkv = new() { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Resolution = 0, Subtitles = SubtitleMode.Softsub };

    private static List<TrackPlan> Plan() =>
    [
        new() { File = F, Stream = 0, Kind = "video" },
        new() { File = F, Stream = 1, Kind = "audio", Language = "ita", Output = "Opus", Encoder = "libopus" },
        new() { File = F, Stream = 2, Kind = "audio", Language = "jpn", Output = "Copy", Include = false },
        new() { File = F, Stream = 3, Kind = "subtitle", Language = "ita", Output = "ass" },
    ];

    [Fact]
    public void A_duplicated_track_is_encoded_twice_with_its_own_settings_and_title()
    {
        var l = Plan();
        int dup = TrackTabs.Duplicate(l, 1);
        Assert.Equal(2, dup);
        Assert.Equal(1, l[dup].Copy);
        Assert.False(l[dup].Default);
        Assert.Equal("Italiano (Opus)", l[dup].Title);
        // Picking Vorbis on the copy: the title KnA wrote follows the codec.
        l[dup] = TrackTabs.Retitle(l[dup], l[dup] with { Output = "Vorbis", Encoder = "libvorbis" });
        Assert.Equal("Italiano (Vorbis)", l[dup].Title);
        // A title written by hand stays.
        var hand = l[dup] with { Title = "Mine" };
        Assert.Equal("Mine", TrackTabs.Retitle(hand, hand with { Output = "Aac" }).Title);

        var a = CommandBuilder.Compile(Ctx(Mkv, l)).Plan.Arguments.ToList();
        Assert.Equal("libopus", a[a.IndexOf("-c:a:0") + 1]);
        Assert.Equal("libvorbis", a[a.IndexOf("-c:a:1") + 1]);
        Assert.Equal(2, a.Select((x, i) => (x, i)).Count(p => p.x == "-map" && a[p.i + 1].EndsWith(":1", StringComparison.Ordinal)));
        Assert.Contains("title=Italiano (Vorbis)", a);
        Assert.NotEqual(l[1].Key, l[dup].Key);
        Assert.Equal(l[1].SourceKey, l[dup].SourceKey);
    }

    [Fact]
    public void Pre_encoded_audio_belongs_to_one_copy_only()
    {
        var l = Plan();
        TrackTabs.Duplicate(l, 1);
        var a = CommandBuilder.Compile(Ctx(Mkv, l, new Dictionary<string, string> { [l[2].Key] = "/w/fdkaac0.m4a" })).Plan.Arguments.ToList();
        Assert.Equal("libopus", a[a.IndexOf("-c:a:0") + 1]);   // the original is still encoded by FFmpeg
        Assert.Equal("copy", a[a.IndexOf("-c:a:1") + 1]);      // the copy takes its pre-encoded file
        Assert.Contains("/w/fdkaac0.m4a", a);
    }

    [Fact]
    public void Each_track_has_its_own_audio_filters()
    {
        var l = Plan();
        TrackTabs.Duplicate(l, 1);
        l[1] = l[1] with { Filters = new AudioFilterSettings { GainDb = 3 } };
        var a = CommandBuilder.Compile(Ctx(Mkv, l)).Plan.Arguments.ToList();
        int f0 = a.IndexOf("-filter:a:0");
        Assert.True(f0 >= 0);
        Assert.Contains("volume=3", a[f0 + 1]);
        int f1 = a.IndexOf("-filter:a:1");
        Assert.True(f1 < 0 || !a[f1 + 1].Contains("volume", StringComparison.Ordinal));
        // A track without filters of its own follows the job's (presets, automatic plans).
        Assert.Equal(new AudioFilterSettings { Normalize = true }, AudioPlanner.FiltersFor(l[2], Mkv with { AudioFilters = new() { Normalize = true } }));
        Assert.Equal(3, AudioPlanner.FiltersFor(l[1], Mkv with { AudioFilters = new() { Normalize = true } }).GainDb);
    }

    [Fact]
    public void Plus_cross_and_arrows_change_the_output_tracks()
    {
        var l = Plan();
        Assert.Equal([1], TrackTabs.Active(l, "audio"));
        Assert.Equal([2], TrackTabs.Inactive(l, "audio"));
        int at = TrackTabs.Activate(l, 2);
        Assert.Equal(2, at);
        Assert.Equal([1, 2], TrackTabs.Active(l, "audio"));
        Assert.Equal(1, TrackTabs.Move(l, 2, -1));
        Assert.Equal("jpn", l[1].Language);                 // first in the output now
        Assert.Equal(1, TrackTabs.Move(l, 1, -1));          // already first: nothing moves
        int dup = TrackTabs.Duplicate(l, 1);
        Assert.Empty(TrackTabs.Inactive(l, "audio"));       // copies are never offered by "+"
        TrackTabs.Remove(l, dup);                            // ✕ on a copy deletes it
        Assert.Equal(4, l.Count);
        TrackTabs.Remove(l, 1);                              // ✕ on a file's track leaves it out
        Assert.False(l[1].Include);
        Assert.Equal([1], TrackTabs.Inactive(l, "audio"));
        Assert.Equal("#1 ita → Opus", TrackTabs.TabLabel(l[2], F));
    }

    [Fact]
    public void Subtitles_duplicate_with_another_format_and_one_burn()
    {
        var l = Plan();
        int dup = TrackTabs.Duplicate(l, 3);
        l[dup] = TrackTabs.Retitle(l[dup], l[dup] with { Output = "srt" });
        Assert.Equal("Italiano (SRT)", l[dup].Title);
        var a = CommandBuilder.Compile(Ctx(Mkv, l)).Plan.Arguments.ToList();
        Assert.Equal("ass", a[a.IndexOf("-c:s:0") + 1]);
        Assert.Equal("srt", a[a.IndexOf("-c:s:1") + 1]);
        TrackTabs.Burn(l, dup);
        Assert.True(l[dup].Burn);
        Assert.False(l[dup].Include);
        Assert.True(TrackTabs.IsActive(l[dup]));
        Assert.True(l[3].Include);
    }

    [Fact]
    public void Complete_keeps_copies_and_adds_only_missing_streams()
    {
        var l = Plan();
        TrackTabs.Duplicate(l, 1);
        var done = TrackPlanner.Complete(l, Media());
        Assert.Equal(l.Count, done.Count);
        Assert.Equal(2, done.Count(t => t.Stream == 1));
    }

    [Fact]
    public void Job_wide_filters_become_track_filters_and_back_for_presets()
    {
        var merged = TrackTabs.MergeFilters(new AudioFilterSettings { GainDb = 2, SampleRate = 44100 }, new AudioFilterSettings { Normalize = true, DownmixStereo = true });
        Assert.Equal(2, merged.GainDb);
        Assert.True(merged.Normalize);
        Assert.Equal(44100, merged.SampleRate);
        Assert.False(merged.DownmixStereo);   // the channel count of the track says it

        var tracks = new List<TrackPlan>
        {
            new() { File = F, Stream = 1, Kind = "audio", Output = "Aac", Channels = 2, Filters = new AudioFilterSettings { Normalize = true } },
            new() { File = F, Stream = 2, Kind = "audio", Output = "Aac", Channels = 2, Filters = new AudioFilterSettings { Normalize = true } },
        };
        var preset = EditorModel.ForPreset(Mkv, new JobEdit { Tracks = tracks });
        Assert.True(preset.AudioFilters.Normalize);
        Assert.True(preset.AudioFilters.DownmixStereo);
        tracks[1] = tracks[1] with { Filters = null };
        Assert.False(EditorModel.ForPreset(Mkv, new JobEdit { Tracks = tracks }).AudioFilters.Normalize);
    }

    [Fact]
    public void Importing_a_preset_with_audio_replaces_the_tracks_filters()
    {
        var e = new JobEdit { Tracks = [new() { File = F, Stream = 1, Kind = "audio", Output = "Aac", Filters = new AudioFilterSettings { GainDb = 4 } }] };
        var preset = new Preset("p", null, Mkv with { AudioFilters = new() { Normalize = true } });
        var (s, edit) = EditorModel.ApplyPreset(Mkv, e, preset, PresetParts.Audio);
        Assert.Null(edit.Tracks![0].Filters);
        Assert.True(s.AudioFilters.Normalize);
        // A video-only import keeps them.
        (_, edit) = EditorModel.ApplyPreset(Mkv, e, preset, PresetParts.Video);
        Assert.Equal(4, edit.Tracks![0].Filters!.GainDb);
    }
}
