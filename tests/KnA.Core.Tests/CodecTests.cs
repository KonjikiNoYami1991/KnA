using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.Core.Tests;

/// <summary>Professional / lossless video codecs, explicit audio codecs, encoder choice and GPU validation.</summary>
public class CodecTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    private QueueService NewQueue(string name) => new(new SettingsStore(Path.Combine(fx.Dir, "data-codec-" + name)), restoreQueue: false);

    private async Task<MediaInfo> EncodeAsync(string name, JobSettings s)
    {
        using var q = NewQueue(name);
        var job = Assert.Single(q.AddPaths([fx.Black], s));
        await q.SetEditAsync(job.Id, new JobEdit { TrimEnd = 1.5 });
        var done = new TaskCompletionSource();
        q.QueueChanged += () => { if (!q.IsRunning) done.TrySetResult(); };
        q.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(4));
        Assert.True(job.Status is JobStatus.Done or JobStatus.Warning, job.Message + "\n" + job.Log);
        return await new MediaProber(fx.Tools!.Ffprobe).ProbeAsync(job.OutputPath!, requireVideo: false);
    }

    [Theory]
    [InlineData(VideoCodec.ProRes, null, "mov", "prores", "pcm_s24le")]
    [InlineData(VideoCodec.DNxHR, "mxf", "mxf", "dnxhd", "pcm_s24le")]
    [InlineData(VideoCodec.FFV1, "mkv", "mkv", "ffv1", "flac")]
    [InlineData(VideoCodec.CineForm, null, "mov", "cfhd", "pcm_s24le")]
    [InlineData(VideoCodec.UtVideo, null, "mkv", "utvideo", "flac")]
    [InlineData(VideoCodec.FFVHuff, "avi", "avi", "ffvhuff", "pcm_s24le")]
    [InlineData(VideoCodec.Uncompressed, null, "mov", "v210", "pcm_s24le")]
    public async Task Professional_codecs_encode(VideoCodec codec, string? container, string ext, string videoCodec, string audioCodec)
    {
        if (fx.Tools is null) return;
        var m = await EncodeAsync($"pro-{codec}", new JobSettings
        {
            Profile = ProfileKind.Professional, Codec = codec, Container = container, Quality = QualityLevel.Normal,
            Resolution = 0, Subtitles = SubtitleMode.None,
        });
        Assert.EndsWith("." + ext, m.Path);
        Assert.Equal(videoCodec, m.PrimaryVideo!.CodecName);
        Assert.Equal(audioCodec, m.Audio[0].CodecName);
    }

    [Fact]
    public async Task Vp9_in_webm_with_opus()
    {
        if (fx.Tools is null) return;
        var m = await EncodeAsync("vp9", new JobSettings
        {
            Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.VP9, Container = "webm", Quality = QualityLevel.Draft,
            Resolution = 360, Subtitles = SubtitleMode.None, TenBit = false,
        });
        Assert.Equal("vp9", m.PrimaryVideo!.CodecName);
        Assert.Equal("opus", m.Audio[0].CodecName);
    }

    [Theory]
    [InlineData(AudioCodec.Opus, "opus")]
    [InlineData(AudioCodec.Flac, "flac")]
    [InlineData(AudioCodec.Eac3, "eac3")]
    [InlineData(AudioCodec.Pcm24, "pcm_s24le")]
    [InlineData(AudioCodec.Mp3, "mp3")]
    [InlineData(AudioCodec.Vorbis, "vorbis")]
    [InlineData(AudioCodec.Aac, "aac")]
    public async Task Explicit_audio_codecs(AudioCodec audio, string expected)
    {
        if (fx.Tools is null) return;
        var m = await EncodeAsync($"audio-{audio}", new JobSettings
        {
            Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 360,
            Subtitles = SubtitleMode.None, AudioCodec = audio,
        });
        Assert.Equal(expected, m.Audio[0].CodecName);
    }

    [Fact]
    public async Task He_aac_through_fdkaac_when_available()
    {
        if (fx.Tools?.Fdkaac is null) return;
        var m = await EncodeAsync("heaac", new JobSettings
        {
            Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 360,
            Subtitles = SubtitleMode.None, AudioCodec = AudioCodec.HeAac,
        });
        Assert.Equal("aac", m.Audio[0].CodecName);
    }

    [Fact]
    public void Best_aac_encoder_order()
    {
        Assert.Equal("libfdk_aac", new AudioEncoderSet(["aac", "aac_at", "libfdk_aac"], true).Best(AudioCodec.Aac));
        Assert.Equal("aac_at", new AudioEncoderSet(["aac", "aac_at"], true).Best(AudioCodec.Aac));
        Assert.Equal("fdkaac", new AudioEncoderSet(["aac"], true).Best(AudioCodec.Aac));
        Assert.Equal("aac", new AudioEncoderSet(["aac"], false).Best(AudioCodec.Aac));
        Assert.Null(new AudioEncoderSet(["aac"], false).Best(AudioCodec.HeAac));
    }

    [Fact]
    public void He_aac_without_encoder_falls_back_to_lc_with_warning()
    {
        var plan = AudioPlanner.Plan(new AudioStream { CodecName = "flac", Channels = 2, Index = 1 },
            new JobSettings { AudioCodec = AudioCodec.HeAac }, "mkv", new AudioEncoderSet(["aac"], false));
        Assert.Equal("aac", plan.Encoder);
        Assert.False(plan.HeAac);
        Assert.False(plan.Issue!.IsError);
    }

    [Fact]
    public void Audio_codec_not_accepted_by_container_is_an_error()
    {
        var plan = AudioPlanner.Plan(new AudioStream { CodecName = "aac", Channels = 2, Index = 1 },
            new JobSettings { AudioCodec = AudioCodec.Flac }, "webm", AudioEncoderSet.Default);
        Assert.True(plan.Issue!.IsError);
    }

    private static CompiledJob CompileFor(JobSettings s, FfmpegCapabilities? caps = null)
    {
        var media = new MediaInfo
        {
            Path = "/x/a.mkv", Duration = TimeSpan.FromSeconds(10),
            Video = [new VideoStream { Kind = "video", Index = 0, CodecName = "h264", Width = 1920, Height = 1080, FrameRate = 24, BitDepth = 8 }],
            Audio = [new AudioStream { Kind = "audio", Index = 1, CodecName = "aac", Channels = 2 }],
        };
        return CommandBuilder.Compile(new CompileContext { Source = media, Settings = s, Options = new BuildOptions { Capabilities = caps } });
    }

    [Fact]
    public void Gpu_backend_without_encoder_is_an_error()
    {
        var c = CompileFor(new JobSettings { Profile = ProfileKind.Professional, Codec = VideoCodec.FFV1, Backend = EncoderBackend.Nvenc });
        Assert.True(c.HasErrors);
        Assert.True(c.Issues.Any(i => i.IsError && i.Message.Contains("NVENC")), string.Join("\n", c.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void Gpu_encoder_that_failed_detection_is_an_error()
    {
        var caps = new FfmpegCapabilities("test", [new EncoderCapability(VideoCodec.HEVC, EncoderBackend.Nvenc, "hevc_nvenc", true, false, false, "No NVENC capable devices found")],
            true, DateTimeOffset.Now) { Schema = FfmpegCapabilities.CurrentSchema };
        var c = CompileFor(new JobSettings { Codec = VideoCodec.HEVC, Backend = EncoderBackend.Nvenc }, caps);
        Assert.True(c.Issues.Any(i => i.IsError && i.Message.Contains("hevc_nvenc")), string.Join("\n", c.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void Detected_alternative_encoder_is_used()
    {
        var caps = new FfmpegCapabilities("test",
        [
            new EncoderCapability(VideoCodec.AV1, EncoderBackend.Software, "libsvtav1", false, false, false, "Not compiled"),
            new EncoderCapability(VideoCodec.AV1, EncoderBackend.Software, "libaom-av1", true, true, true, null),
        ], true, DateTimeOffset.Now) { Schema = FfmpegCapabilities.CurrentSchema };
        var c = CompileFor(new JobSettings { Codec = VideoCodec.AV1 }, caps);
        Assert.False(c.HasErrors, string.Join("\n", c.Issues.Select(i => i.Message)));
        Assert.Contains("libaom-av1", c.Plan.Arguments);
    }

    [Fact]
    public void ProRes_profile_from_quality_and_chroma()
    {
        VideoEncodeRequest R(QualityLevel q, ChromaFormat c = ChromaFormat.Auto) =>
            new(VideoCodec.ProRes, EncoderBackend.Software, q, true, ProfileKind.Professional, 24, false) { Pro = new ProSettings { Chroma = c } };
        Assert.Equal(ProResProfile.Hq, VideoEncoders.ProResFor(R(QualityLevel.High)));
        Assert.Equal(ProResProfile.Proxy, VideoEncoders.ProResFor(R(QualityLevel.Draft)));
        Assert.Equal(ProResProfile.P4444Xq, VideoEncoders.ProResFor(R(QualityLevel.VeryHigh, ChromaFormat.Yuv444)));
        Assert.Contains("yuv444p10le", VideoEncoders.BuildArgs(R(QualityLevel.Normal, ChromaFormat.Yuv444)));
    }

    [Fact]
    public async Task Detection_tests_candidates_and_reports_builds()
    {
        if (fx.Tools is null) return;
        var caps = await HardwareDetector.DetectAsync(fx.Tools.Ffmpeg, [], null);
        Assert.True(caps.IsCurrent);
        Assert.True(caps.IsUsable(VideoCodec.ProRes, EncoderBackend.Software, true));
        Assert.True(caps.IsUsable(VideoCodec.FFV1, EncoderBackend.Software, false));
        Assert.Contains("aac", caps.Main!.Encoders);
        Assert.Single(caps.Builds);
    }
}
