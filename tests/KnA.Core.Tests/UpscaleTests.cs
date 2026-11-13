using KnA.Core.Ai;
using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.Core.Tests;

public class UpscaleTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    private static CompiledJob Compile(JobSettings s, UpscaleEnvironment? env, Tools.FfmpegCapabilities? caps = null)
    {
        var media = new MediaInfo
        {
            Path = "/x/a.mkv", Duration = TimeSpan.FromSeconds(10),
            Video = [new VideoStream { Kind = "video", Index = 0, CodecName = "h264", Width = 640, Height = 360, FrameRate = 24, FrameRateRational = "24/1", BitDepth = 8 }],
            Audio = [new AudioStream { Kind = "audio", Index = 1, CodecName = "aac", Channels = 2 }],
        };
        return CommandBuilder.Compile(new CompileContext { Source = media, Settings = s, Upscale = env, Options = new BuildOptions { Capabilities = caps } });
    }

    [Fact]
    public void Enlarge_fits_the_16_9_box()
    {
        var v = new VideoStream { Width = 640, Height = 360 };
        Assert.Equal(new ScaleResult(1920, 1080, true), Upscalers.Enlarge(v, 1080));
        Assert.Null(Upscalers.Enlarge(v, 360));
        Assert.Equal(new ScaleResult(1440, 1080, true), Upscalers.Enlarge(new VideoStream { Width = 640, Height = 480 }, 1080));
    }

    [Fact]
    public void Placebo_replaces_the_scaler_when_enlarging()
    {
        var c = Compile(new JobSettings { Codec = VideoCodec.H264, Resolution = 1080, Subtitles = SubtitleMode.None, Upscale = new() { Method = UpscaleMethod.Placebo } }, new UpscaleEnvironment());
        var graph = c.Plan.Arguments[c.Plan.Arguments.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("libplacebo=w=1920:h=1080", graph);
        Assert.Contains("ewa_lanczossharp", graph);
    }

    [Fact]
    public void Missing_shaders_or_python_are_errors()
    {
        var a4k = Compile(new JobSettings { Resolution = 1080, Upscale = new() { Method = UpscaleMethod.Anime4K } }, new UpscaleEnvironment { ShaderFile = "/nope.glsl" });
        Assert.Contains(a4k.Issues, i => i.IsError && i.Message.Contains("Anime4K"));
        var nn = Compile(new JobSettings { Resolution = 1080, Upscale = new() { Method = UpscaleMethod.Neural } }, new UpscaleEnvironment());
        Assert.Contains(nn.Issues, i => i.IsError && i.Message.Contains("Python"));
    }

    [Fact]
    public void Neural_reads_rgb48_from_the_sidecar()
    {
        var env = new UpscaleEnvironment { Python = "/usr/bin/python3", Script = "/app/ai/kna_upscale.py", ModelPath = "/m/realesr-animevideov3.pth", ModelKnown = true, ModelScale = 4 };
        var c = Compile(new JobSettings { Codec = VideoCodec.H264, Resolution = 1080, Subtitles = SubtitleMode.None, Upscale = new() { Method = UpscaleMethod.Neural } }, env);
        Assert.False(c.HasErrors, string.Join("\n", c.Issues.Select(i => i.Message)));
        Assert.Contains("rgb48le", c.Plan.Arguments);
        Assert.Contains("2560x1440", c.Plan.Arguments);
        Assert.NotNull(c.Pipe?.Command);
        Assert.Equal("/usr/bin/python3", c.Pipe!.Command![0]);
        Assert.Contains("--model", c.Pipe.Command);
    }

    /// <summary>Real model run: set KNA_TEST_PYTHON to a Python with torch + spandrel.</summary>
    [Fact]
    public async Task Neural_upscale_end_to_end()
    {
        var python = Environment.GetEnvironmentVariable("KNA_TEST_PYTHON");
        if (fx.Tools is null || python is null || !File.Exists(python)) return;
        var src = Path.Combine(fx.Dir, "small.mkv");
        await Processing.ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24:duration=1",
            "-f", "lavfi", "-i", "sine=duration=1", "-c:v", "libx264", "-c:a", "aac", src]);
        using var q = new QueueService(new SettingsStore(Path.Combine(fx.Dir, "data-ai")), restoreQueue: false);
        q.Settings.Ai = q.Settings.Ai with { Python = python, Device = "cpu", Fp16 = false };
        var job = Assert.Single(q.AddPaths([src], new JobSettings
        {
            Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 360, Subtitles = SubtitleMode.None,
            Upscale = new UpscaleSettings { Method = UpscaleMethod.Neural, Model = "realesr-animevideov3" },
        }));
        var done = new TaskCompletionSource();
        q.QueueChanged += () => { if (!q.IsRunning) done.TrySetResult(); };
        q.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(10));
        Assert.True(job.Status is JobStatus.Done or JobStatus.Warning, job.Message + "\n" + job.Log);
        var m = await new MediaProber(fx.Tools.Ffprobe).ProbeAsync(job.OutputPath!);
        Assert.Equal(360, m.PrimaryVideo!.Height);   // 160×90 ×4 = 640×360
        var count = await Processing.ProcessRunner.RunAsync(fx.Tools.Ffprobe, ["-v", "error", "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", job.OutputPath!]);
        Assert.True(job.Log.Contains("[kna-ai] done: 24 frames"), string.Join("\n", job.Log.Split('\n').Where(l => l.Contains("kna-ai") || l.Contains("Command"))));
        Assert.Equal("24", count.StdOut.Trim());
    }
}
