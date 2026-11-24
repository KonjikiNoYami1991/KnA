using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Queue;
using Xunit;

namespace KnA.Core.Tests;

/// <summary>HDR carried through every path by itself: scripts, trims, crops, chunked encodes, GPU encoders.</summary>
public class HdrPathTests
{
    private static readonly MasteringDisplay Md = new(0.68, 0.32, 0.265, 0.69, 0.15, 0.06, 0.3127, 0.329, 0.0001, 1000);

    private static VideoStream Source(HdrInfo hdr, double fps = 23.976) => new()
    {
        Index = 0, Kind = "video", CodecName = "hevc", Width = 3840, Height = 2160, FrameRate = fps, BitDepth = 10,
        ColorPrimaries = "bt2020", ColorTransfer = hdr.IsHlg ? "arib-std-b67" : "smpte2084", ColorSpace = "bt2020nc",
        ColorRange = "tv", ChromaLocation = "topleft", Hdr = hdr,
    };

    private static readonly HdrInfo Hdr10 = new() { Format = HdrFormat.Hdr10, Mastering = Md, MaxCll = 1000, MaxFall = 400 };
    private static readonly HdrInfo Dv81 = Hdr10 with { Format = HdrFormat.DolbyVision, DolbyVisionProfile = 8, DolbyVisionCompatibility = 1 };
    private static readonly HdrInfo Hdr10Plus = Hdr10 with { Format = HdrFormat.Hdr10Plus, HasHdr10Plus = true };
    private static readonly HdrEnvironment Tools = new(true, true, true, true);
    private static readonly HdrSideFiles Files = new("/w/RPU.kept.bin", "/w/hdr10plus.kept.json", "/w/RPU.bin");

    private static MediaInfo Media(VideoStream v, string path = "/m/film.mkv", double minutes = 100) => new()
    {
        Path = path, Duration = TimeSpan.FromMinutes(minutes), Video = [v],
        Audio = path.EndsWith(".mkv") ? [new AudioStream { Index = 1, Kind = "audio", CodecName = "ac3", Channels = 6, Language = "ita" }] : [],
    };

    /// <summary>The script's video: what vspipe --info says (no colours, no HDR).</summary>
    private static MediaInfo Script(int w = 3840, int h = 2160, double fps = 23.976, long frames = 143856) => Media(new VideoStream
    {
        Index = 0, Kind = "video", CodecName = "vapoursynth", Width = w, Height = h, FrameRate = fps, FrameCount = frames, BitDepth = 10,
    }, "/w/job.vpy", frames / fps / 60);

    private static readonly JobSettings X265 = new()
    {
        Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Backend = EncoderBackend.Software, TenBit = true, Resolution = 0,
        Subtitles = SubtitleMode.None, Hdr = HdrMode.Auto,
    };

    private static CompileContext Ctx(MediaInfo src, JobSettings s, MediaInfo? script = null, JobEdit? edit = null, HdrSideFiles? files = null)
    {
        var media = new Dictionary<string, MediaInfo> { [src.Path] = src };
        var e = edit ?? new JobEdit();
        if (script is not null)
        {
            media[script.Path] = script;
            var tracks = TrackPlanner.Default(src, s).Select(t => t.Kind == "video" ? t with { File = script.Path, Stream = 0 } : t).ToList();
            e = e with { Tracks = tracks };
        }
        return new CompileContext
        {
            Source = src, Media = media, Settings = s, Edit = e, Hdr = Tools, HdrFiles = files,
            Options = new BuildOptions { RigayaEncoders = new Dictionary<EncoderBackend, string> { [EncoderBackend.Nvenc] = "/t/NVEncC64" } },
        };
    }

    private static string X265Params(CompiledJob j) => j.Plan.Arguments[j.Plan.Arguments.ToList().IndexOf("-x265-params") + 1];

    [Fact]
    public void Script_video_takes_the_sources_hdr_and_x265_gets_explicit_metadata()
    {
        var j = CommandBuilder.Compile(Ctx(Media(Source(Dv81)), X265, Script(), files: Files));
        Assert.False(j.HasErrors, string.Join("\n", j.Issues.Select(i => i.Message)));
        Assert.Equal(HdrAction.Preserve, j.Hdr.Action);
        var x = X265Params(j);
        Assert.Contains("master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)", x);
        Assert.Contains("max-cll=1000,400", x);
        Assert.Contains("colorprim=bt2020", x);
        Assert.Contains("chromaloc=2", x);
        Assert.Contains("dolby-vision-profile=8.1", x);
        Assert.Contains("dolby-vision-rpu=/w/RPU.kept.bin", x);
        Assert.Contains("smpte2084", j.Plan.Arguments);
        Assert.Contains(j.Hdr.Details, d => d.Contains("script", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Script_that_changes_the_frame_count_keeps_static_hdr_and_says_why()
    {
        // QTGMC double rate: twice the frames at twice the rate.
        var j = CommandBuilder.Compile(Ctx(Media(Source(Dv81)), X265, Script(fps: 47.952, frames: 287712), files: Files));
        Assert.Equal(HdrCarry.None, j.Hdr.DolbyVision);
        Assert.Equal(HdrAction.Preserve, j.Hdr.Action);
        Assert.Contains("master-display=", X265Params(j));
        Assert.DoesNotContain("dolby-vision", X265Params(j));
        Assert.Contains(j.Issues, i => i.Message.Contains("frame count"));
    }

    [Fact]
    public void Script_that_crops_resets_the_rpu_active_area()
    {
        var j = CommandBuilder.Compile(Ctx(Media(Source(Dv81)), X265, Script(3840, 1600), files: Files));
        Assert.True(j.Hdr.RpuCrop);
        Assert.Equal(HdrCarry.Encoder, j.Hdr.DolbyVision);
    }

    [Fact]
    public void Tone_mapping_of_script_frames_knows_they_are_pq()
    {
        var j = CommandBuilder.Compile(Ctx(Media(Source(Hdr10)), X265 with { Hdr = HdrMode.ToneMapSdr }, Script()));
        Assert.Equal(HdrAction.ToneMap, j.Hdr.Action);
        Assert.StartsWith("setparams=color_primaries=bt2020:color_trc=smpte2084", j.Hdr.Filter);
        var cmd = string.Join(' ', j.Plan.Arguments);
        Assert.DoesNotContain("master-display", cmd);
        Assert.DoesNotContain("bt2020", cmd.Replace(j.Hdr.Filter!, ""));
        Assert.Contains("bt709", j.Plan.Arguments);
    }

    [Fact]
    public void Rigaya_reading_a_script_gets_explicit_values_never_copy()
    {
        var s = X265 with { Backend = EncoderBackend.Nvenc, Encoder = new EncoderOverrides { Program = EncoderProgram.Rigaya } };
        var j = CommandBuilder.Compile(Ctx(Media(Source(Dv81)), s, Script(), files: Files));
        var a = j.Rigaya!.EncoderArgs.ToList();
        Assert.Equal("G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1)", a[a.IndexOf("--master-display") + 1]);
        Assert.Equal("1000,400", a[a.IndexOf("--max-cll") + 1]);
        Assert.Equal("2", a[a.IndexOf("--chromaloc") + 1]);
        Assert.Equal("smpte2084", a[a.IndexOf("--transfer") + 1]);
        Assert.DoesNotContain("copy", a);
        Assert.Equal("8.1", a[a.IndexOf("--dolby-vision-profile") + 1]);
    }

    [Fact]
    public void Hlg_dolby_vision_becomes_profile_8_4_and_av1_profile_10()
    {
        var hlgDv = new HdrInfo { Format = HdrFormat.DolbyVision, DolbyVisionProfile = 8, DolbyVisionCompatibility = 4 };
        var hevc = HdrPlanner.Plan(Source(hlgDv), X265, false, new HdrRoute(), Tools);
        Assert.Equal("8.4", hevc.DolbyVisionProfile);
        var av1 = HdrPlanner.Plan(Source(Dv81), X265 with { Codec = VideoCodec.AV1, Backend = EncoderBackend.Nvenc }, false, new HdrRoute { Rigaya = true }, Tools);
        Assert.Equal("10.1", av1.DolbyVisionProfile);
        Assert.Equal(HdrCarry.Encoder, av1.DolbyVision);
    }

    [Fact]
    public void Encoders_that_cannot_take_the_metadata_get_it_injected()
    {
        // FFmpeg's hevc_nvenc, chunked x265: injected into the finished HEVC.
        var nv = HdrPlanner.Plan(Source(Dv81 with { HasHdr10Plus = true }), X265 with { Backend = EncoderBackend.Nvenc }, false, new HdrRoute(), Tools);
        Assert.Equal(HdrCarry.Inject, nv.DolbyVision);
        Assert.Equal(HdrCarry.Inject, nv.Hdr10Plus);
        Assert.True(nv.NeedsInjection);
        var chunked = HdrPlanner.Plan(Source(Hdr10Plus), X265, false, new HdrRoute { Chunked = true }, Tools);
        Assert.Equal(HdrCarry.Inject, chunked.Hdr10Plus);
        Assert.False(chunked.EncoderReadsSideFiles);
        // HDR10+ with rigaya's HEVC: injected (a JSON file would need its extra hdr10plus_gen module).
        var rig = HdrPlanner.Plan(Source(Hdr10Plus), X265 with { Backend = EncoderBackend.Nvenc }, false, new HdrRoute { Rigaya = true }, Tools);
        Assert.Equal(HdrCarry.Inject, rig.Hdr10Plus);
        // AV1 outside rigaya's encoders: dovi_tool can't add it afterwards → static only, said.
        var svt = HdrPlanner.Plan(Source(Dv81), X265 with { Codec = VideoCodec.AV1 }, false, new HdrRoute(), Tools);
        Assert.Equal(HdrCarry.None, svt.DolbyVision);
        Assert.NotEmpty(svt.Issues);
        // Variable frame rate: the raw stream can't be remuxed with its timing.
        var vfr = HdrPlanner.Plan(Source(Dv81), X265 with { Backend = EncoderBackend.Nvenc }, false, new HdrRoute { VariableFrameRate = true }, Tools);
        Assert.Equal(HdrCarry.None, vfr.DolbyVision);
    }

    [Fact]
    public void Sdr_output_gets_no_hdr_parameter()
    {
        var plan = HdrPlanner.Plan(Source(Dv81 with { HasHdr10Plus = true }), X265 with { Codec = VideoCodec.H264, TenBit = false }, false, new HdrRoute(), Tools);
        Assert.Equal(HdrAction.ToneMap, plan.Action);
        Assert.Equal(HdrCarry.None, plan.DolbyVision);
        Assert.Equal(HdrCarry.None, plan.Hdr10Plus);
        Assert.Empty(plan.X265Params);
        Assert.Contains("bt709", plan.ColorArgs);
    }

    [Fact]
    public void Trim_keeps_dynamic_metadata_for_the_kept_frames_only()
    {
        var edit = new JobEdit { TrimStart = 60, TrimEnd = 120 };
        var j = CommandBuilder.Compile(Ctx(Media(Source(Dv81 with { HasHdr10Plus = true })), X265, edit: edit, files: Files));
        Assert.Equal(HdrCarry.Encoder, j.Hdr.DolbyVision);
        Assert.Equal(HdrCarry.Encoder, j.Hdr.Hdr10Plus);
        Assert.Equal([(60.0, 120.0)], j.Hdr.KeptSeconds!);
        Assert.Contains("dhdr10-info=/w/hdr10plus.kept.json", X265Params(j));
        // A crop: the active area follows.
        var c = CommandBuilder.Compile(Ctx(Media(Source(Dv81)), X265 with { VideoFilters = new VideoFilterSettings { Crop = new CropSettings(0, 280, 0, 280) } }, files: Files));
        Assert.True(c.Hdr.RpuCrop);
        // A frame-rate change can't follow.
        var fps = CommandBuilder.Compile(Ctx(Media(Source(Dv81)), X265 with { VideoFilters = new VideoFilterSettings { Fps = 25 } }, files: Files));
        Assert.Equal(HdrCarry.None, fps.Hdr.DolbyVision);
        Assert.Contains(fps.Issues, i => i.Message.Contains("frame rate"));
    }

    [Fact]
    public void Frame_ranges_to_remove_match_ffmpegs_trim()
    {
        Assert.Equal(["0-99", "200-299"], HdrMetadata.RemoveRanges([(10, 20)], 10, 300));
        Assert.Equal(["0-9", "20-49", "60-99"], HdrMetadata.RemoveRanges([(1, 2), (5, 6)], 10, 100));
        Assert.Equal(["240-299"], HdrMetadata.RemoveRanges([(0, 24)], 10, 300));
        Assert.Empty(HdrMetadata.RemoveRanges([(0, 30)], 10, 300));
        Assert.Equal(100, HdrMetadata.KeptCount(HdrMetadata.RemoveRanges([(10, 20)], 10, 300), 300));
        // 23.976: the first kept frame is the first at or after the start.
        Assert.Equal("0-1438", HdrMetadata.RemoveRanges([(60, 120)], 24000 / 1001.0, 10000)[0]);
    }

    [Fact]
    public void Rpu_frame_count_is_read_from_the_summary()
    {
        Assert.Equal(143856, HdrMetadata.ParseRpuCount("Parsing RPU file...\nSummary:\n  Frames: 143856\n  Profile: 8\n"));
        Assert.Equal(0, HdrMetadata.ParseRpuCount("error"));
    }

    [Fact]
    public void Standard_frameserver_now_reads_hdr_sources_except_profile_5()
    {
        var s = X265;
        Assert.Null(StandardFrameserver.Blocker(s, Media(Source(Hdr10)), "/m/film.mkv", new JobEdit()));
        var p5 = new HdrInfo { Format = HdrFormat.DolbyVision, DolbyVisionProfile = 5, DolbyVisionCompatibility = 0 };
        Assert.NotNull(StandardFrameserver.Blocker(s, Media(Source(p5)), "/m/film.mkv", new JobEdit()));
    }
}
