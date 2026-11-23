using KnA.Core.Encoding;
using KnA.Core.Media;
using Xunit;

namespace KnA.Core.Tests;

/// <summary>GPU-first encodes with rigaya's encoders: KnA's filters become --vpp-* filters, nothing falls back silently.</summary>
public class GpuFirstTests
{
    private const string F = "/m/film.mkv";

    private static VideoStream Video(string codec = "h264", HdrInfo? hdr = null, int w = 1920, int h = 1080) => new()
    {
        Index = 0, Kind = "video", CodecName = codec, Width = w, Height = h, FrameRate = 25, FrameCount = 25 * 600, BitDepth = hdr is null ? 8 : 10, Hdr = hdr ?? HdrInfo.Sdr,
    };

    private static MediaInfo Media(VideoStream v, string path = F) => new()
    {
        Path = path, Duration = TimeSpan.FromMinutes(10), Video = [v],
        Audio = [new AudioStream { Index = 1, Kind = "audio", CodecName = "aac", Channels = 2 }],
        Subtitles = [new SubtitleStream { Index = 2, Kind = "subtitle", CodecName = "subrip", Language = "eng" },
                     new SubtitleStream { Index = 3, Kind = "subtitle", CodecName = "hdmv_pgs_subtitle", Language = "ita" }],
    };

    private static CompileContext Ctx(MediaInfo m, JobSettings s, JobEdit? edit = null, Dictionary<string, MediaInfo>? extra = null)
    {
        var media = new Dictionary<string, MediaInfo> { [m.Path] = m };
        if (extra is not null) foreach (var (k, v) in extra) media[k] = v;
        return new CompileContext
        {
            Source = m, Media = media, Settings = s, Edit = edit ?? new(),
            Options = new BuildOptions { RigayaEncoders = new Dictionary<EncoderBackend, string> { [EncoderBackend.Nvenc] = "/t/nvencc", [EncoderBackend.Qsv] = "/t/qsvencc", [EncoderBackend.Amf] = "/t/vceencc" } },
        };
    }

    private static readonly JobSettings Rig = new()
    {
        Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Backend = EncoderBackend.Nvenc, Resolution = 0, Subtitles = SubtitleMode.None,
        Encoder = new EncoderOverrides { Program = EncoderProgram.Rigaya },
    };

    private static List<string> Native(CompiledJob j) => Assert.IsAssignableFrom<IReadOnlyList<string>>(j.Rigaya!.NativeArgs).ToList();

    [Fact]
    public void Burned_text_and_picture_subtitles_run_on_the_gpu()
    {
        var plan = new List<TrackPlan>
        {
            new() { File = F, Stream = 0, Kind = "video" },
            new() { File = F, Stream = 2, Kind = "subtitle", Include = false, Burn = true, Codec = "subrip" },
        };
        var text = CommandBuilder.Compile(Ctx(Media(Video()), Rig, new JobEdit { Tracks = plan }) with { Burn = new PreparedSubtitles("sub.ass", "fonts", null) });
        var n = Native(text);
        Assert.Equal("filename=sub.ass,fontsdir=fonts", n[n.IndexOf("--vpp-subburn") + 1]);

        // PGS of the same file: the encoder reads it itself (2nd subtitle track of the file → track=2).
        plan[1] = plan[1] with { Stream = 3, Codec = "hdmv_pgs_subtitle" };
        var pgs = CommandBuilder.Compile(Ctx(Media(Video()), Rig, new JobEdit { Tracks = plan }) with { Burn = new PreparedSubtitles(null, null, 3, F) });
        n = Native(pgs);
        Assert.Equal("track=2", n[n.IndexOf("--vpp-subburn") + 1]);

        // With a delay the picture track can't be shifted inside the encoder: FFmpeg overlays it, and says why.
        var late = CommandBuilder.Compile(Ctx(Media(Video()), Rig, new JobEdit { Tracks = plan }) with { Burn = new PreparedSubtitles(null, null, 3, F, 1.5) });
        Assert.Null(late.Rigaya!.NativeArgs);
        Assert.Contains("picture subtitles", late.Rigaya.PipeReason);
    }

    [Fact]
    public void Hdr10_to_sdr_uses_the_encoders_tone_mapping()
    {
        var hdr = new HdrInfo { Format = HdrFormat.Hdr10, MaxCll = 1200, MaxFall = 300 };
        var s = Rig with { Codec = VideoCodec.H264, Hdr = HdrMode.ToneMapSdr, ToneMap = ToneMapCurve.Hable };
        var j = CommandBuilder.Compile(Ctx(Media(Video("hevc", hdr, 3840, 2160)), s));
        var n = Native(j);
        Assert.Equal("hdr2sdr=hable,source_peak=1200,ldr_nits=100", n[n.IndexOf("--vpp-colorspace") + 1]);
        Assert.Equal("bt709", n[n.IndexOf("--colorprim") + 1]);
        // A curve the encoder lacks: the closest one, and the editor says so.
        var spline = CommandBuilder.Compile(Ctx(Media(Video("hevc", hdr, 3840, 2160)), s with { ToneMap = ToneMapCurve.Spline }));
        Assert.Contains("hdr2sdr=bt2390", string.Join(' ', Native(spline)));
        Assert.Contains(spline.Gpu!.Steps, x => x.Note?.Contains("BT.2390") == true);
        // HLG: no hdr2sdr for it → FFmpeg.
        var hlg = CommandBuilder.Compile(Ctx(Media(Video("hevc", new HdrInfo { Format = HdrFormat.Hlg }, 3840, 2160)), s));
        Assert.Null(hlg.Rigaya!.NativeArgs);
        Assert.Contains("HLG", hlg.Rigaya.PipeReason);
    }

    [Fact]
    public void Formats_the_gpu_cannot_decode_are_decoded_by_the_encoder_on_the_cpu()
    {
        var j = CommandBuilder.Compile(Ctx(Media(Video("mpeg4")), Rig with { VideoFilters = new VideoFilterSettings { Denoise = FilterStrength.Medium } }));
        var n = Native(j);
        Assert.Equal("--avsw", n[0]);
        Assert.Contains("--vpp-hqdn3d", n);
        Assert.Contains(j.Gpu!.Steps, x => x.Option == "--avsw" && x.Place == StepPlace.CpuFfmpeg);
    }

    [Fact]
    public void Filters_without_a_gpu_equivalent_are_named_with_their_alternative()
    {
        var j = CommandBuilder.Compile(Ctx(Media(Video()), Rig with { VideoFilters = new VideoFilterSettings { Custom = "eq=saturation=1.2", Fps = 23.976 } }));
        Assert.Null(j.Rigaya!.NativeArgs);
        Assert.Contains("custom FFmpeg filters", j.Rigaya.PipeReason);
        Assert.Contains("frame rate change", j.Rigaya.PipeReason);
        Assert.False(j.Gpu!.AllOnGpu);
        Assert.Equal(StepPlace.Gpu, j.Gpu.Steps[^1].Place);                                          // the encoder itself
        Assert.DoesNotContain(j.Gpu.Steps.Take(j.Gpu.Steps.Count - 1), x => x.Place == StepPlace.Gpu);   // FFmpeg does the rest
        Assert.Contains(j.Gpu.Steps, x => x.GpuAlternative is not null && x.Place == StepPlace.CpuFfmpeg);
        Assert.Contains(j.Issues, x => !x.IsError && x.Message.Contains("NVEncC"));
    }

    [Fact]
    public void Scripts_are_read_by_the_encoder_and_pure_gpu_chains_skip_the_script()
    {
        const string script = "/w/kna.vpy";
        var sv = new VideoStream { Index = 0, Kind = "video", CodecName = "wrapped_avframe", Width = 1280, Height = 720, FrameRate = 25, FrameCount = 15000 };
        var sm = new MediaInfo { Path = script, Duration = TimeSpan.FromMinutes(10), Video = [sv] };
        var tracks = new List<TrackPlan> { new() { File = script, Stream = 0, Kind = "video" }, new() { File = F, Stream = 0, Kind = "video", Include = false } };

        // QTGMC has no exact GPU twin: the encoder reads the script (--vpy) and KnA proposes --vpp-rtgmc.
        var qtgmc = Rig with
        {
            Frameserver = new FrameserverSettings { Kind = FrameserverKind.VapourSynth, Chain = [new FrameserverFilter("vs.qtgmc"), new FrameserverFilter("vs.crop", new() { ["top"] = "20", ["bottom"] = "20" })] },
            VideoFilters = new VideoFilterSettings { Sharpen = FilterStrength.Medium },
        };
        var j = CommandBuilder.Compile(Ctx(Media(Video()), qtgmc, new JobEdit { Tracks = tracks }, new() { [script] = sm }));
        var n = Native(j);
        Assert.Equal(["--vpy", "-i", script], n.Take(3));
        Assert.Contains("--vpp-cas", n);                                       // KnA's own filters still on the GPU
        Assert.Contains(j.Gpu!.Steps, x => x.Name == "QTGMC" && x.Place == StepPlace.CpuScript && x.GpuAlternative!.StartsWith("--vpp-rtgmc", StringComparison.Ordinal));
        Assert.True(j.Gpu.UsesScript);

        // Bwdif + crop + resize: the same filters exist on the GPU, so the script is not needed to run.
        var pure = Rig with
        {
            Frameserver = new FrameserverSettings
            {
                Kind = FrameserverKind.VapourSynth,
                Chain = [new FrameserverFilter("vs.bwdif", new() { ["field"] = "3" }), new FrameserverFilter("vs.crop", new() { ["top"] = "20", ["bottom"] = "20" }),
                         new FrameserverFilter("vs.resize", new() { ["kernel"] = "Spline64", ["width"] = "1280", ["height"] = "720" })],
            },
        };
        j = CommandBuilder.Compile(Ctx(Media(Video()), pure, new JobEdit { Tracks = tracks }, new() { [script] = sm }));
        n = Native(j);
        Assert.Equal(["--avhw", "-i", F], n.Take(3));
        Assert.Equal("mode=bob,order=tff", n[n.IndexOf("--vpp-bwdif") + 1]);
        Assert.Equal("0,20,0,20", n[n.IndexOf("--crop") + 1]);
        Assert.Equal("1280x720", n[n.IndexOf("--output-res") + 1]);
        Assert.Equal("spline64", n[n.IndexOf("--vpp-resize") + 1]);
        Assert.True(j.Gpu!.AllOnGpu);
        Assert.Contains("GPU", j.Gpu.Summary);
    }

    [Fact]
    public void Catalogue_filters_have_gpu_alternatives()
    {
        Assert.Equal("--vpp-rtgmc", RigayaFilters.Alternative("vs.qtgmc")!.Value.Option);
        Assert.Equal("--vpp-nlmeans", RigayaFilters.Alternative("avs.knlm")!.Value.Option);
        Assert.Equal("--vpp-degrain", RigayaFilters.Alternative("vs.smdegrain")!.Value.Option);
        Assert.True(RigayaFilters.Alternative("vs.cas")!.Value.Same);
        Assert.Null(RigayaFilters.Alternative("vs.grain"));
        Assert.Null(RigayaFilters.ChainOnGpu(new FrameserverSettings { Kind = FrameserverKind.VapourSynth, Chain = [new FrameserverFilter("vs.knlm")] }, EncoderBackend.Nvenc));
    }

    [Fact]
    public void Bwdif_on_flagged_frames_only_where_the_encoder_has_it()
    {
        var interlaced = new VideoStream { Index = 0, Kind = "video", CodecName = "mpeg2video", Width = 720, Height = 576, FrameRate = 25, FrameCount = 15000, FieldOrder = "tt" };
        var nv = Native(CommandBuilder.Compile(Ctx(Media(interlaced), Rig)));
        Assert.Equal("deint=interlaced", nv[nv.IndexOf("--vpp-bwdif") + 1]);
        var qsv = Native(CommandBuilder.Compile(Ctx(Media(interlaced), Rig with { Backend = EncoderBackend.Qsv })));
        int i = qsv.IndexOf("--vpp-bwdif");
        Assert.True(i >= 0 && (i + 1 == qsv.Count || qsv[i + 1].StartsWith('-')));   // QSVEncC 8.38 has no deint=
    }
}

/// <summary>Speed / quality profiles of rigaya's encoders and the starting values from the detected GPU.</summary>
public class RigayaProfileTests
{
    private static KnA.Core.Tools.FfmpegCapabilities Caps(bool av1) => new("x",
        [new KnA.Core.Tools.EncoderCapability(VideoCodec.HEVC, EncoderBackend.Nvenc, "hevc_nvenc", true, true, true, null),
         new KnA.Core.Tools.EncoderCapability(VideoCodec.AV1, EncoderBackend.Nvenc, "av1_nvenc", true, av1, av1, av1 ? null : "No capable devices found")],
        true, DateTimeOffset.Now) { Schema = KnA.Core.Tools.FfmpegCapabilities.CurrentSchema };

    [Fact]
    public void A_profile_fills_the_fields_and_keeps_options_set_by_hand()
    {
        var best = RigayaProfiles.For(EncoderBackend.Nvenc, VideoCodec.HEVC).Single(p => p.Id == "best");
        var o = RigayaProfiles.Apply(new EncoderOverrides { Extra = "--bref-mode each --split-enc auto_forced" }, best);
        Assert.Equal("P7", o.Speed);
        Assert.Equal("uhq", o.Tune);
        Assert.Equal(32, o.Lookahead);
        Assert.Equal("--bref-mode each --aq-temporal", o.Extra);   // the user's option stays, the old profile's goes
        Assert.Equal("best", RigayaProfiles.Current(o, EncoderBackend.Nvenc, VideoCodec.HEVC)!.Id);
        Assert.Null(RigayaProfiles.Current(o with { Speed = "P6" }, EncoderBackend.Nvenc, VideoCodec.HEVC));
        // H.264 has no UHQ tune; HEVC/AV1 "Fast" splits frames between NVENC engines.
        Assert.Equal("hq", RigayaProfiles.For(EncoderBackend.Nvenc, VideoCodec.H264).Single(p => p.Id == "best").Tune);
        Assert.Equal("--split-enc auto_forced", RigayaProfiles.For(EncoderBackend.Nvenc, VideoCodec.AV1).Single(p => p.Id == "fast").Extra);
        // The values reach the encoder.
        var args = RigayaEncoders.EncoderArgs(EncoderBackend.Nvenc, VideoCodec.HEVC, QualityLevel.Normal, true, o, 24, new HdrPlan(),
            new VideoStream { Index = 0, Kind = "video", CodecName = "hevc", Width = 1920, Height = 1080, FrameRate = 24 }, false);
        Assert.Equal(["-u", "P7", "--tune", "uhq", "--aq", "--lookahead", "32"], args.Skip(args.IndexOf("-u")).Take(7));
        Assert.Contains("--aq-temporal", args);
        Assert.Equal(3, RigayaProfiles.For(EncoderBackend.Qsv, VideoCodec.HEVC).Count);
        Assert.Equal("slower", RigayaProfiles.For(EncoderBackend.Amf, VideoCodec.H264)[0].Speed);
    }

    [Fact]
    public void Starting_values_follow_the_gpu_generation()
    {
        Assert.Equal("best", RigayaProfiles.Recommended(EncoderBackend.Nvenc, VideoCodec.HEVC, QualityLevel.VeryHigh, Caps(av1: true)).Id);
        Assert.Equal("balanced", RigayaProfiles.Recommended(EncoderBackend.Nvenc, VideoCodec.HEVC, QualityLevel.VeryHigh, Caps(av1: false)).Id);
        Assert.Equal("balanced", RigayaProfiles.Recommended(EncoderBackend.Nvenc, VideoCodec.HEVC, QualityLevel.Normal, Caps(av1: true)).Id);
        Assert.Equal("fast", RigayaProfiles.Recommended(EncoderBackend.Nvenc, VideoCodec.HEVC, QualityLevel.BelowNormal, Caps(av1: false)).Id);
        Assert.Equal("fast", RigayaProfiles.Recommended(EncoderBackend.Nvenc, VideoCodec.HEVC, QualityLevel.Draft, null).Id);
    }

    [Fact]
    public void Av1_only_where_the_gpu_encodes_it_in_hardware()
    {
        var m = new MediaInfo
        {
            Path = "/m/a.mkv", Duration = TimeSpan.FromMinutes(1),
            Video = [new VideoStream { Index = 0, Kind = "video", CodecName = "h264", Width = 1920, Height = 1080, FrameRate = 25, FrameCount = 1500 }],
        };
        CompiledJob Compile(bool av1) => CommandBuilder.Compile(new CompileContext
        {
            Source = m, Media = new Dictionary<string, MediaInfo> { [m.Path] = m },
            Settings = new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.AV1, Backend = EncoderBackend.Nvenc, Resolution = 0, Subtitles = SubtitleMode.None, Encoder = new EncoderOverrides { Program = EncoderProgram.Rigaya } },
            Options = new BuildOptions { Capabilities = Caps(av1), RigayaEncoders = new Dictionary<EncoderBackend, string> { [EncoderBackend.Nvenc] = "/t/nvencc" } },
        });
        Assert.Contains(Compile(false).Issues, i => i.IsError && i.Message.Contains("AV1"));
        Assert.DoesNotContain(Compile(true).Issues, i => i.IsError);
    }
}
