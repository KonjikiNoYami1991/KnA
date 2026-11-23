using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;
using Xunit;

namespace KnA.Core.Tests;

/// <summary>The customization editor's model (explicit settings, per-track encoders, GPU and frameserver filters) and the detailed wizard.</summary>
public class EditorModelUnitTests
{
    private static MediaInfo Media() => new()
    {
        Path = "/m/film.mkv", Duration = TimeSpan.FromMinutes(24),
        Video = [new VideoStream { Index = 0, Kind = "video", CodecName = "h264", Width = 1920, Height = 1080, FrameRate = 23.976 }],
        Audio = [new AudioStream { Index = 1, Kind = "audio", CodecName = "flac", Channels = 6, Language = "jpn" }],
        Subtitles = [new SubtitleStream { Index = 2, Kind = "subtitle", CodecName = "ass", Language = "ita" }],
    };

    private static CompileContext Ctx(MediaInfo m, JobSettings s, JobEdit edit, FfmpegCapabilities? caps = null) => new()
    {
        Source = m, Media = new Dictionary<string, MediaInfo> { [m.Path] = m }, Settings = s, Edit = edit,
        Options = new BuildOptions
        {
            Capabilities = caps,
            AudioEncoders = new AudioEncoderSet(["aac", "libfdk_aac", "libopus", "ac3", "flac", "libmp3lame"]),
        },
    };

    private static List<TrackPlan> Tracks(string? output = null, string? encoder = null, int? bitrate = null, int? channels = null, string? args = null) =>
    [
        new() { File = "/m/film.mkv", Stream = 0, Kind = "video" },
        new() { File = "/m/film.mkv", Stream = 1, Kind = "audio", Output = output, Encoder = encoder, Bitrate = bitrate, Channels = channels, Arguments = args },
        new() { File = "/m/film.mkv", Stream = 2, Kind = "subtitle", Output = "srt", Arguments = "-flags +bitexact" },
    ];

    [Fact]
    public void Per_track_encoder_bitrate_channels_and_parameters_reach_the_command()
    {
        var s = new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Resolution = 0, Subtitles = SubtitleMode.Softsub };
        var c = CommandBuilder.Compile(Ctx(Media(), s, new JobEdit { Tracks = Tracks("Aac", "aac", 128, 2, "-aac_coder twoloop") }));
        var a = c.Plan.Arguments.ToList();
        int i = a.IndexOf("-c:a:0");
        Assert.Equal("aac", a[i + 1]);                                   // the chosen encoder, not the "best" libfdk_aac
        Assert.Equal("2", a[a.IndexOf("-ac:a:0") + 1]);                  // downmixed to stereo
        Assert.Equal("128k", a[a.IndexOf("-b:a:0") + 1]);
        Assert.Equal("twoloop", a[a.IndexOf("-aac_coder:a:0") + 1]);
        Assert.Equal("srt", a[a.IndexOf("-c:s:0") + 1]);
        Assert.Equal("+bitexact", a[a.IndexOf("-flags:s:0") + 1]);

        // An encoder that is not available falls back to the best one for the codec.
        c = CommandBuilder.Compile(Ctx(Media(), s, new JobEdit { Tracks = Tracks("Aac", "aac_at") }));
        Assert.Equal("libfdk_aac", c.Plan.Arguments.ToList()[c.Plan.Arguments.ToList().IndexOf("-c:a:0") + 1]);
    }

    [Fact]
    public void A_codec_chosen_for_a_track_ignores_the_job_wide_bitrate()
    {
        var s = new JobSettings { Profile = ProfileKind.UniversalMkv, AudioBitrate = 64 };
        var t = new TrackPlan { File = "x", Stream = 1, Kind = "audio", Output = "Opus" };
        Assert.Null(AudioPlanner.ForTrack(t, s).AudioBitrate);
        Assert.Equal(96, AudioPlanner.ForTrack(t with { Bitrate = 96 }, s).AudioBitrate);
        Assert.Equal(64, AudioPlanner.ForTrack(t with { Output = null }, s).AudioBitrate);
    }

    [Fact]
    public void Audio_encoders_are_listed_per_codec_best_first()
    {
        var set = new AudioEncoderSet(["aac", "aac_at", "libopus"], ExternalFdkaac: true);
        Assert.Equal(["aac_at", "fdkaac", "aac"], set.Available(AudioCodec.Aac));
        Assert.Equal(AudioCodec.Aac, AudioEncoderSet.CodecOf("fdkaac"));
        Assert.Equal(AudioCodec.Opus, AudioEncoderSet.CodecOf("libopus"));
        Assert.Empty(set.Available(AudioCodec.Mp3));
    }

    [Fact]
    public void Gpu_filters_upload_the_frames_before_the_hardware_encoder()
    {
        var caps = new FfmpegCapabilities("x", [new EncoderCapability(VideoCodec.HEVC, EncoderBackend.Nvenc, "hevc_nvenc", true, true, true, null)], true, DateTimeOffset.Now)
        { Schema = FfmpegCapabilities.CurrentSchema };
        var s = new JobSettings
        {
            Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Backend = EncoderBackend.Nvenc, Resolution = 0, Subtitles = SubtitleMode.None, TenBit = true,
            VideoFilters = new VideoFilterSettings { Gpu = "scale_cuda=w=1280:h=720" },
        };
        var a = CommandBuilder.Compile(Ctx(Media(), s, new JobEdit(), caps)).Plan.Arguments.ToList();
        var graph = a[a.IndexOf("-filter_complex") + 1];
        Assert.Contains("format=p010le,hwupload,scale_cuda=w=1280:h=720", graph);
        Assert.Equal("cuda=cu", a[a.IndexOf("-init_hw_device") + 1]);
        Assert.DoesNotContain("-pix_fmt", a);
        Assert.Equal("hevc_nvenc", a[a.IndexOf("-c:v") + 1]);

        // VA-API already uploads at the end of the chain: the GPU filters come after it.
        s = s with { Backend = EncoderBackend.Vaapi, VideoFilters = new VideoFilterSettings { Gpu = "denoise_vaapi=denoise=20" } };
        caps = caps with { Encoders = [new EncoderCapability(VideoCodec.HEVC, EncoderBackend.Vaapi, "hevc_vaapi", true, true, true, null)] };
        a = CommandBuilder.Compile(Ctx(Media(), s, new JobEdit(), caps)).Plan.Arguments.ToList();
        Assert.Contains("hwupload,denoise_vaapi=denoise=20", a[a.IndexOf("-filter_complex") + 1]);
    }

    [Fact]
    public void Rigaya_gpu_filters_are_encoder_options()
    {
        var o = new EncoderOverrides { Program = EncoderProgram.Rigaya, GpuFilters = "--vpp-resize spline36 --vpp-nlmeans" };
        var v = new VideoStream { Index = 0, Kind = "video", CodecName = "h264", Width = 1920, Height = 1080, FrameRate = 24 };
        var a = RigayaEncoders.EncoderArgs(EncoderBackend.Nvenc, VideoCodec.HEVC, QualityLevel.Normal, true, o, 24, new HdrPlan(), v, native: false);
        Assert.Equal("spline36", a[a.IndexOf("--vpp-resize") + 1]);
        Assert.Contains("--vpp-nlmeans", a);
        Assert.True(RigayaEncoders.IsOwnedOption("--vpp-resize"));
        Assert.True(RigayaEncoders.IsOwnedOption("--cqp"));
        Assert.False(RigayaEncoders.IsOwnedOption("--aq-temporal"));
    }

    [Fact]
    public void Quality_levels_become_visible_values()
    {
        var (q, speed) = VideoEncoders.AutoValues(new VideoEncodeRequest(VideoCodec.HEVC, EncoderBackend.Software, QualityLevel.Normal, true, ProfileKind.UniversalMkv, 24, false));
        Assert.NotNull(q);
        Assert.NotNull(speed);
        Assert.Equal(RigayaEncoders.DefaultQuality("nvencc", VideoCodec.HEVC, QualityLevel.High), RigayaEncoders.DefaultQuality("nvencc", VideoCodec.HEVC, QualityLevel.High));
        Assert.True(RigayaEncoders.DefaultQuality("nvencc", VideoCodec.HEVC, QualityLevel.VeryHigh) < RigayaEncoders.DefaultQuality("nvencc", VideoCodec.HEVC, QualityLevel.Low));
        Assert.Contains(RigayaEncoders.DefaultSpeed("qsvencc", QualityLevel.Normal), RigayaEncoders.SpeedValues("qsvencc"));
    }

    [Fact]
    public void Profile_import_resets_the_video_to_what_the_profile_allows()
    {
        var s = new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, TenBit = true, Container = "mkv",
            Encoder = new EncoderOverrides { RateControl = RateControl.Quality, QualityValue = 18, Extra = "-x265-params aq-mode=3" } };
        var e = new JobEdit { Tracks = Tracks("Opus", "libopus", 96) };
        var (s2, e2) = EditorModel.ApplyProfile(s, e, ProfileKind.BlurayAac);
        Assert.Equal(VideoCodec.H264, s2.Codec);        // Blu-ray AAC cannot use HEVC (profile definition)
        Assert.False(s2.TenBit);
        Assert.Null(s2.Container);
        Assert.True(s2.Encoder.IsEmpty);
        var audio = e2.Tracks!.Single(t => t.Kind == "audio");
        Assert.Null(audio.Output);                       // the profile chooses the audio again
        Assert.Null(audio.Encoder);
        Assert.Null(e2.Tracks!.Single(t => t.Kind == "subtitle").Output);
    }

    [Fact]
    public void A_preset_saved_from_the_editor_carries_the_track_choices()
    {
        var s = new JobSettings { Profile = ProfileKind.UniversalMkv, Subtitles = SubtitleMode.Hardsub };
        var tracks = Tracks("Opus", "libopus", 112, null, "-vbr on");
        var p = EditorModel.ForPreset(s, new JobEdit { Tracks = tracks });
        Assert.Equal(AudioCodec.Opus, p.AudioCodec);
        Assert.Equal(112, p.AudioBitrate);
        Assert.Equal("-vbr on", p.Arguments.AudioFfmpeg);
        Assert.Equal(SubtitleMode.Softsub, p.Subtitles);   // a kept subtitle track, none burned
        Assert.Equal("-flags +bitexact", p.Arguments.Subtitles);
    }

    [Fact]
    public void Only_containers_that_hold_every_final_stream_are_offered()
    {
        var m = Media();
        var media = new Dictionary<string, MediaInfo> { [m.Path] = m };
        var enc = new AudioEncoderSet(["aac", "libopus", "flac"]);
        var s = new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.AV1 };
        // AV1 alone fits mkv, mp4, webm, ts (not mov… which holds AV1 too) — FLAC copied rules out webm and ts.
        var copyFlac = Tracks("Copy");
        copyFlac[2] = copyFlac[2] with { Include = false };
        var allowed = Containers.AllowedFor(s, copyFlac, media, enc);
        Assert.Contains("mkv", allowed);
        Assert.DoesNotContain("webm", allowed);
        Assert.DoesNotContain("ts", allowed);
        // Opus + WebVTT: WebM is possible again, MP4 cannot take WebVTT.
        var web = Tracks("Opus", "libopus");
        web[2] = web[2] with { Output = "webvtt" };
        allowed = Containers.AllowedFor(s, web, media, enc);
        Assert.Contains("webm", allowed);
        Assert.DoesNotContain("mp4", allowed);
        // An attachment needs Matroska.
        var att = Tracks("Opus", "libopus");
        att.Add(new TrackPlan { File = "/m/font.ttf", Stream = -1, Kind = "attachment" });
        Assert.Equal(["mkv"], Containers.AllowedFor(s, att, media, enc));
    }
}

public class FilterCatalogTests
{
    [Fact]
    public void A_vapoursynth_chain_becomes_a_script()
    {
        var fs = new FrameserverSettings
        {
            Kind = FrameserverKind.VapourSynth, Source = "bs", AudioFromScript = true,
            Chain = [new("vs.qtgmc", new() { ["preset"] = "Slow", ["tff"] = "false" }), new("vs.resize", new() { ["kernel"] = "Lanczos", ["width"] = "99999" }), new("nope")],
        };
        var script = fs.EffectiveScript!;
        Assert.Contains("import havsfunc as haf", script);
        Assert.Contains("clip = core.bs.VideoSource(source=r\"{source}\")", script);
        Assert.Contains("haf.QTGMC(clip, Preset=\"Slow\", TFF=False, FPSDivisor=1)", script);
        Assert.Contains("core.resize.Lanczos(clip, width=16384, height=1080)", script);   // clamped to the range
        Assert.Contains("audio.set_output(1)", script);
        Assert.DoesNotContain("nope", script);
    }

    [Fact]
    public void An_avisynth_chain_becomes_a_script_and_hand_written_scripts_stay()
    {
        var fs = new FrameserverSettings { Kind = FrameserverKind.AviSynth, Chain = [new("avs.crop", new() { ["left"] = "8", ["right"] = "8" }), new("avs.qtgmc")] };
        var script = fs.EffectiveScript!;
        Assert.StartsWith("LWLibavVideoSource(\"{source}\")", script);
        Assert.Contains("Crop(8, 0, -8, -0)", script);
        Assert.Contains("AssumeTFF()\nQTGMC(Preset=\"Slower\", FPSDivisor=1)", script);
        Assert.Equal("my script", (fs with { Chain = null, Script = "my script" }).EffectiveScript);
        // every template placeholder has a parameter
        foreach (var f in FilterCatalog.All)
            Assert.DoesNotContain("{", FilterCatalog.Line(f.Kind, new FrameserverFilter(f.Id))!.Replace("{source}", ""));
    }
}

public class ParameterPagesTests
{
    [Fact]
    public void Filter_options_and_chains_are_read_and_written()
    {
        const string help = """
            Filter bwdif
              Deinterlace the input image.
            bwdif AVOptions:
               mode              <int>        ..FV....... specify the interlacing mode (from 0 to 1) (default send_field)
                 send_frame      0            ..FV....... send one frame for each frame
                 send_field      1            ..FV....... send one frame for each field
               deint             <int>        ..FV....... specify which frames to deinterlace (from 0 to 1) (default all)
            """;
        var o = ToolHelp.ParseFfmpeg(help, null, filter: true);
        Assert.Equal(["mode", "deint"], o.Select(x => x.Name));
        Assert.Equal(["send_frame", "send_field"], o[0].Values);
        Assert.Equal("send_field", o[0].Default);

        var chain = FilterChain.Parse("scale_cuda=w=1280:h=720,bwdif_cuda=mode=1,hwdownload");
        Assert.Equal(3, chain.Count);
        Assert.Equal("720", chain[0].Options.Single(x => x.Key == "h").Value);
        Assert.Equal("scale_cuda=w=1280:h=720,bwdif_cuda=mode=1,hwdownload", FilterChain.Format(chain));
    }

    [Fact]
    public void Options_are_grouped_into_pages()
    {
        string Cat(string name, string? section = null) => new ToolOption(name, "x", "", []) { Section = section }.Category;
        Assert.Equal("Rate control", Cat("-crf_max"));
        Assert.Equal("Rate control", Cat("--qvbr-quality"));
        Assert.Equal("GOP / frames", Cat("--gop-len"));
        Assert.Equal("Colour / HDR", Cat("--master-display"));
        Assert.Equal("Filters", Cat("--vpp-nlmeans"));
        Assert.Equal("Analysis / tools", Cat("-subme"));
        Assert.Equal("Input / output", Cat("--audio-copy"));
        Assert.True(VideoEncoders.IsOwnedOption("-crf"));
        Assert.True(VideoEncoders.IsGpuFilter("scale_cuda", EncoderBackend.Nvenc));
        Assert.False(VideoEncoders.IsGpuFilter("hwupload_cuda", EncoderBackend.Nvenc));
        Assert.False(VideoEncoders.IsGpuFilter("scale_vaapi", EncoderBackend.Nvenc));

        var gnu = ToolHelp.ParseGnu("""
            Encode Mode Options:
               --qvbr <float>              set bitrate in QVBR mode
               --aq                        enable adaptive quantization

            Frame buffer Options:
               --output-buf <int>          output buffer size (default: 8)
            """);
        Assert.Equal("Encode Mode Options", gnu.Single(x => x.Name == "--aq").Section);
        Assert.Equal("8", gnu.Single(x => x.Name == "--output-buf").Default);
    }
}

public class DetailedWizardTests
{
    private static FfmpegCapabilities Caps() => new("x",
    [
        new EncoderCapability(VideoCodec.HEVC, EncoderBackend.Nvenc, "hevc_nvenc", true, true, true, null),
        new EncoderCapability(VideoCodec.HEVC, EncoderBackend.Qsv, "hevc_qsv", true, false, false, "no device"),
    ], true, DateTimeOffset.Now);

    [Fact]
    public void Steps_follow_the_answers_and_only_working_gpus_can_be_picked()
    {
        var steps = PresetWizard.Steps(new Dictionary<string, string> { ["goal"] = "Archive", ["codec"] = "HEVC" }, Caps());
        var encoder = steps.Single(s => s.Key == "encoder");
        Assert.True(encoder.Choices.Single(c => c.Value == "Nvenc").Enabled);
        Assert.False(encoder.Choices.Single(c => c.Value == "Qsv").Enabled);   // compiled, but no such GPU
        Assert.Contains(steps, s => s.Key == "quality");
        Assert.Equal(WizardStepKind.Summary, steps[^1].Kind);

        steps = PresetWizard.Steps(new Dictionary<string, string> { ["goal"] = "Archive", ["rate"] = "size" }, Caps());
        Assert.Contains(steps, s => s.Key == "size" && s.Kind == WizardStepKind.Number);
        Assert.DoesNotContain(steps, s => s.Key == "quality");

        steps = PresetWizard.Steps(new Dictionary<string, string> { ["goal"] = "Tv" }, Caps());
        Assert.Contains(steps, s => s.Key == "tvAudio");
        Assert.DoesNotContain(steps, s => s.Key == "codec");   // Blu-ray profiles are H.264 only
        Assert.DoesNotContain(steps, s => s.Key == "tenBit");

        steps = PresetWizard.Steps(new Dictionary<string, string> { ["goal"] = "Extract" }, Caps());
        Assert.Equal(["goal", "name"], steps.Select(s => s.Key));
    }

    [Fact]
    public void Detailed_answers_build_detailed_settings()
    {
        var s = PresetWizard.Build(new Dictionary<string, string>
        {
            ["goal"] = "Archive", ["content"] = "animation", ["codec"] = "HEVC", ["encoder"] = "Software", ["tenBit"] = "true",
            ["rate"] = "crf", ["crf"] = "19.5", ["resolution"] = "1080", ["cleanup"] = "deband", ["audioCodec"] = "Opus", ["audioBitrate"] = "128",
            ["mix"] = "normalize", ["audioLanguages"] = "jpn, ita", ["dropCommentary"] = "true", ["subtitles"] = "Softsub", ["ocr"] = "true", ["chunks"] = "true",
        }, Caps());
        Assert.Equal(ProfileKind.UniversalMkv, s.Profile);
        Assert.Equal("animation", s.Encoder.Tune);
        Assert.Equal(RateControl.Quality, s.Encoder.RateControl);
        Assert.Equal(19.5, s.Encoder.QualityValue);
        Assert.Equal(FilterStrength.Light, s.VideoFilters.Deband);
        Assert.Equal(AudioCodec.Opus, s.AudioCodec);
        Assert.Equal(128, s.AudioBitrate);
        Assert.True(s.AudioFilters.Normalize);
        Assert.Equal("jpn, ita", s.TrackRules.AudioLanguages);
        Assert.True(s.TrackRules.DropCommentary);
        Assert.True(s.OcrPictureSubtitles);
        Assert.True(s.Chunks.Enabled);
        Assert.Contains(PresetWizard.Summary(s), l => l.Contains("19.5") || l.Contains("19,5"));

        var tv = PresetWizard.Build(new Dictionary<string, string> { ["goal"] = "Tv", ["tvAudio"] = "Ac3", ["encoder"] = "Qsv" }, Caps());
        Assert.Equal(ProfileKind.BlurayAc3, tv.Profile);
        Assert.Equal(EncoderBackend.Software, tv.Backend);   // QSV did not pass the test (and has no H.264 here)
    }
}

public class EditorMaterializeTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    [Fact]
    public async Task The_editor_turns_the_profile_choices_into_explicit_values()
    {
        if (fx.Tools is null) return;
        using var q = new QueueService(new SettingsStore(Path.Combine(fx.Dir, "data-editor")), restoreQueue: false);
        var job = Assert.Single(q.AddPaths([fx.Black], new JobSettings
        {
            Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Resolution = 0, Subtitles = SubtitleMode.None,
            Arguments = new ToolArguments { AudioFfmpeg = "-aac_coder twoloop" },
        }));
        var (s, e, a) = await EditorModel.MaterializeAsync(q, job.Id, job.Settings, job.Edit);
        Assert.NotNull(e.Tracks);
        var audio = e.Tracks!.Single(t => t.Kind == "audio" && t.Include);
        Assert.NotNull(audio.Output);                                  // copy or a codec, never "automatic"
        Assert.Equal(RateControl.Quality, s.Encoder.RateControl);      // the quality level became a CRF value
        Assert.NotNull(s.Encoder.QualityValue);
        Assert.NotNull(s.Encoder.Speed);
        Assert.Equal("mp4", s.Container);
        Assert.Null(s.Arguments.AudioFfmpeg);                          // moved onto the track that uses it
        if (!string.Equals(audio.Output, "Copy", StringComparison.OrdinalIgnoreCase)) Assert.Equal("-aac_coder twoloop", audio.Arguments);
        Assert.DoesNotContain(a.Issues, i => i.IsError);
        Assert.Contains("mp4", a.AllowedContainers);

        // A profile import resets the video and lets the profile choose the audio again.
        var (s2, e2) = EditorModel.ApplyProfile(s, e, ProfileKind.BlurayAc3);
        var (s3, e3, a3) = await EditorModel.MaterializeAsync(q, job.Id, s2, e2);
        Assert.Equal(ProfileKind.BlurayAc3, s3.Profile);
        Assert.Equal("Ac3", e3.Tracks!.Single(t => t.Kind == "audio" && t.Include).Output);
        Assert.Contains("-c:a:0 ac3", a3.CommandLine);
    }
}

public class AiSetupTests
{
    [Fact]
    public async Task The_connection_check_explains_what_is_wrong()
    {
        // A port where nothing listens: the check says so instead of a raw socket error.
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var c = await KnA.Core.Ai.AiSetup.CheckAsync(new KnA.Core.Ai.AiSettings { Provider = "custom", BaseUrl = $"http://127.0.0.1:{port}/v1" });
        Assert.False(c.Ok);
        Assert.NotNull(c.Hint);
        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Hint);

        Assert.True(KnA.Core.Ai.AiSetup.IsVision("qwen2.5vl:7b"));
        Assert.True(KnA.Core.Ai.AiSetup.IsVision("llava:13b"));
        Assert.False(KnA.Core.Ai.AiSetup.IsVision("qwen2.5:7b"));
        Assert.NotEmpty(KnA.Core.Ai.AiSetup.Install("ollama"));
        Assert.True(KnA.Core.Ai.AiSetup.NeedsKey("openai"));
    }
}

public class ContinuousChunkTests
{
    [Fact]
    public void Segments_vary_and_get_shorter_towards_the_end()
    {
        var t = ChunkedEncoder.SplitTimes(0, 1200, 60, 4).ToList();
        var lengths = t.Prepend(0).Zip(t.Append(1200), (a, b) => b - a).ToList();
        Assert.True(lengths.Count >= 12, "at least three segments per worker");
        Assert.True(lengths.Distinct().Count() > 3, "varied lengths, not all equal");
        Assert.True(lengths.TakeLast(4).Average() < lengths.Take(4).Average(), "shorter at the end");
        Assert.All(lengths, l => Assert.True(l >= 10 * 0.4 - 0.01));
        // one worker: plain steps, as before
        Assert.Equal([60, 120], ChunkedEncoder.SplitTimes(0, 180, 60, 1));
    }

    [Fact]
    public void Every_step_has_its_progress()
    {
        var job = new QueueJob { SourcePath = "/m/a.mkv" };
        job.Status = JobStatus.Running;
        job.BeginStage("Subtitle OCR");
        Assert.True(job.StageIndeterminate);
        job.StagePercent = 40;
        Assert.False(job.StageIndeterminate);
        Assert.Equal(40, job.Percent);
        job.BeginStage("Checking the result");
        Assert.Null(job.StagePercent);
        Assert.Equal(0, job.Percent);
    }
}
