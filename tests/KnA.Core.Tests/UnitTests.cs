using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Processing;
using KnA.Core.Queue;

namespace KnA.Core.Tests;

public class ScaleCalculatorTests
{
    private static VideoStream V(int w, int h, double sar = 1) => new() { Width = w, Height = h, SampleAspectRatio = sar };

    [Fact] public void Downscales_1080p_to_720p() =>
        Assert.Equal(new ScaleResult(1280, 720, true), ScaleCalculator.Compute(V(1920, 1080), 720));

    [Fact] public void Never_upscales() => Assert.Null(ScaleCalculator.Compute(V(640, 360), 1080));

    [Fact] public void Anamorphic_source_becomes_square_pixels() =>
        Assert.Equal(new ScaleResult(1280, 720, true), ScaleCalculator.Compute(V(1440, 1080, 4.0 / 3.0), 720));

    [Fact] public void Odd_dimensions_are_made_even_without_resizing() =>
        Assert.Equal(new ScaleResult(1554, 812, false), ScaleCalculator.Compute(V(1555, 813), 0));

    [Fact] public void Four_by_three_uses_width() =>
        Assert.Equal(new ScaleResult(1280, 960, true), ScaleCalculator.Compute(V(1440, 1080), 720));

    [Fact] public void Portrait_uses_height() =>
        Assert.Equal(new ScaleResult(406, 720, true), ScaleCalculator.Compute(V(1080, 1920), 720));
}

public class VideoEncoderTests
{
    private static List<string> Args(VideoCodec c, EncoderBackend b, QualityLevel q = QualityLevel.Normal, bool tenBit = false) =>
        VideoEncoders.BuildArgs(new VideoEncodeRequest(c, b, q, tenBit, ProfileKind.UniversalMkv, 23.976, false));

    private static string After(List<string> a, string key) => a[a.IndexOf(key) + 1];

    [Fact]
    public void Nvenc_uses_constant_quality_not_crf()
    {
        var a = Args(VideoCodec.AV1, EncoderBackend.Nvenc, tenBit: true);
        Assert.Equal("av1_nvenc", After(a, "-c:v"));
        Assert.Contains("-cq", a);
        Assert.DoesNotContain("-crf", a);
        Assert.Equal("p010le", After(a, "-pix_fmt"));
    }

    [Fact]
    public void Amf_qvbr_scale_is_inverted()
    {
        int best = int.Parse(After(Args(VideoCodec.HEVC, EncoderBackend.Amf, QualityLevel.VeryHigh), "-qvbr_quality_level"));
        int worst = int.Parse(After(Args(VideoCodec.HEVC, EncoderBackend.Amf, QualityLevel.Draft), "-qvbr_quality_level"));
        Assert.True(best > worst, "AMF QVBR: higher level = better quality");
    }

    [Fact]
    public void Crf_scale_is_lower_is_better()
    {
        double best = double.Parse(After(Args(VideoCodec.AV1, EncoderBackend.Software, QualityLevel.VeryHigh), "-crf"), System.Globalization.CultureInfo.InvariantCulture);
        double worst = double.Parse(After(Args(VideoCodec.AV1, EncoderBackend.Software, QualityLevel.Draft), "-crf"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(best < worst);
    }

    [Fact]
    public void Software_10bit_hevc_uses_main10()
    {
        var a = Args(VideoCodec.HEVC, EncoderBackend.Software, tenBit: true);
        Assert.Equal("yuv420p10le", After(a, "-pix_fmt"));
        Assert.Equal("main10", After(a, "-profile:v"));
    }

    [Fact]
    public void Vaapi_uploads_in_filter_chain_instead_of_pix_fmt()
    {
        var a = Args(VideoCodec.AV1, EncoderBackend.Vaapi, tenBit: true);
        Assert.DoesNotContain("-pix_fmt", a);
        Assert.Equal("format=p010,hwupload", VideoEncoders.FilterTail(EncoderBackend.Vaapi, VideoCodec.AV1, true));
        Assert.Contains("-init_hw_device", VideoEncoders.GlobalArgs(EncoderBackend.Vaapi, null));
    }

    [Fact] public void No_av1_hardware_encoder_on_apple() => Assert.Null(VideoEncoders.EncoderName(VideoCodec.AV1, EncoderBackend.VideoToolbox));

    [Fact] public void H264_never_10bit() => Assert.False(VideoEncoders.Supports10Bit(VideoCodec.H264, EncoderBackend.Nvenc));

    [Fact]
    public void Bluray_profile_forces_h264_8bit()
    {
        var s = new JobSettings { Profile = ProfileKind.BlurayAac, Codec = VideoCodec.AV1, TenBit = true, Backend = EncoderBackend.Nvenc };
        Assert.Equal(VideoCodec.H264, s.EffectiveCodec);
        Assert.False(s.EffectiveTenBit);
        Assert.Equal(EncoderBackend.Nvenc, s.EffectiveBackend);
    }

    [Fact]
    public void Xvid_falls_back_to_cpu()
    {
        var s = new JobSettings { Profile = ProfileKind.XvidMp3, Backend = EncoderBackend.Nvenc };
        Assert.Equal(EncoderBackend.Software, s.EffectiveBackend);
    }
}

public class AudioPlannerTests
{
    private static AudioStream A(string codec, int ch, bool def = false) => new() { CodecName = codec, Channels = ch, Index = 1, IsDefault = def };

    [Fact] public void Streaming_downmixes_surround_aac() =>
        Assert.Equal(["-c:a:0", "aac", "-ac:a:0", "2", "-b:a:0", "128k"], AudioPlanner.BuildArgs(A("aac", 6), 0, ProfileKind.Streaming, QualityLevel.Normal));

    [Fact] public void Bluray_aac_copies_aac() =>
        Assert.Equal(["-c:a:0", "copy"], AudioPlanner.BuildArgs(A("aac", 6), 0, ProfileKind.BlurayAac, QualityLevel.Normal));

    [Fact] public void Ac3_bitrate_is_capped() =>
        Assert.Equal("576k", AudioPlanner.BuildArgs(A("dts", 6), 0, ProfileKind.BlurayAc3, QualityLevel.VeryHigh)[^1]);

    [Fact]
    public void Hardsub_keeps_only_default_track()
    {
        var m = new MediaInfo { Path = "x.mkv", Audio = [A("aac", 2), new AudioStream { CodecName = "aac", Channels = 2, Index = 2, IsDefault = true }] };
        Assert.Equal(2, Assert.Single(AudioPlanner.SelectTracks(m, SubtitleMode.Hardsub, ProfileKind.UniversalMkv)).Index);
        Assert.Equal(2, AudioPlanner.SelectTracks(m, SubtitleMode.Softsub, ProfileKind.UniversalMkv).Count);
    }
}

public class ParserTests
{
    [Fact]
    public void Progress_block_is_parsed()
    {
        var p = new ProgressParser(TimeSpan.FromSeconds(100));
        string[] lines = ["frame=250", "fps=50.5", "total_size=1048576", "out_time_us=10000000", "speed=2.5x", "progress=continue"];
        EncodeProgress? result = null;
        foreach (var l in lines) result = p.Feed(l) ?? result;
        Assert.NotNull(result);
        Assert.Equal(10, result!.Percent, 3);
        Assert.Equal(50.5, result.Fps);
        Assert.Equal(36, result.Remaining!.Value.TotalSeconds, 3);
        Assert.Equal(10_485_760, result.EstimatedFinalSize);
    }

    [Fact]
    public void Ffprobe_json_is_parsed()
    {
        const string json = """
        { "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1440, "height": 1080,
              "sample_aspect_ratio": "4:3", "avg_frame_rate": "24000/1001", "pix_fmt": "yuv420p10le", "field_order": "tt",
              "disposition": { "default": 1, "forced": 0, "attached_pic": 0 } },
            { "index": 1, "codec_name": "flac", "codec_type": "audio", "channels": 6, "disposition": { "default": 1 },
              "tags": { "language": "jpn" } },
            { "index": 2, "codec_name": "ass", "codec_type": "subtitle", "disposition": { "default": 0, "forced": 1 } },
            { "index": 3, "codec_name": "ttf", "codec_type": "attachment", "tags": { "filename": "a.ttf", "mimetype": "application/x-truetype-font" } }
          ],
          "format": { "duration": "1440.5", "size": "123", "format_name": "matroska,webm" } }
        """;
        var m = FfprobeParser.Parse("a.mkv", json);
        var v = m.PrimaryVideo!;
        Assert.Equal(1920, v.DisplayWidth, 3);
        Assert.Equal(23.976, v.FrameRate, 3);
        Assert.Equal(10, v.BitDepth);
        Assert.True(v.IsInterlaced);
        Assert.True(m.Audio[0].IsLossless);
        Assert.True(m.Subtitles[0].IsForced);
        Assert.True(m.Attachments[0].IsFont);
        Assert.Equal(34537, v.FrameCount); // computed from duration × fps
    }

    [Fact]
    public void Keyframe_scene_file_is_converted()
    {
        var frames = KeyframeGenerator.ParseSceneFile("frame:0    pts:2002    pts_time:2.002\nlavfi.scene_score=0.5\nframe:1 pts:5005 pts_time:5.005\n", 24000.0 / 1001);
        Assert.Equal([0L, 48L, 120L], frames);
        Assert.StartsWith("# keyframe format v1\nfps 23.976024\n0\n48\n", KeyframeGenerator.FormatAegisub(frames, 24000.0 / 1001));
    }

    [Fact] public void Extra_args_respect_quotes() =>
        Assert.Equal(["-x265-params", "a=1:b=2", "-metadata", "title=Hello World"],
            JobRunner.SplitArgs("-x265-params a=1:b=2 -metadata \"title=Hello World\""));
}

public class OutputNamingTests
{
    [Fact]
    public void Never_overwrites()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var src = Path.Combine(dir, "Ep 01.mkv");
            var first = OutputNaming.Build(src, "Universal MKV HEVC 1080p Normal", "mkv", null);
            Assert.Equal(Path.Combine(dir, "Ep 01 [Universal MKV HEVC 1080p Normal].mkv"), first);
            File.WriteAllText(first, "");
            Assert.EndsWith("(2).mkv", OutputNaming.Build(src, "Universal MKV HEVC 1080p Normal", "mkv", null));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class PortableModeTests
{
    [Fact]
    public void Without_marker_the_program_is_not_portable()
    {
        var dir = Directory.CreateTempSubdirectory("kna-portable-").FullName;
        try { Assert.Null(KnA.Core.Settings.AppEnv.PortableDataDirectory(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Marker_puts_data_next_to_the_program()
    {
        var dir = Directory.CreateTempSubdirectory("kna-portable-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, KnA.Core.Settings.AppEnv.PortableMarker), "");
            var data = KnA.Core.Settings.AppEnv.PortableDataDirectory(dir);
            Assert.Equal(Path.Combine(dir, "data"), data);
            Assert.True(Directory.Exists(data));
            Assert.Empty(Directory.GetFiles(data!));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Forced_portable_works_without_marker()
    {
        var dir = Directory.CreateTempSubdirectory("kna-portable-").FullName;
        try { Assert.Equal(Path.Combine(dir, "data"), KnA.Core.Settings.AppEnv.PortableDataDirectory(dir, forced: true)); }
        finally { Directory.Delete(dir, true); }
    }
}
