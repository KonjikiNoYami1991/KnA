using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Processing;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.Core.Tests;

/// <summary>Creates a small anamorphic, 5.1, ASS+SRT, font-attached MKV once for all integration tests.</summary>
public sealed class SampleFixture : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("kna-it-").FullName;
    public FfmpegTools? Tools { get; }
    public string Sample => Path.Combine(Dir, "Sample Ep 01.mkv");

    public SampleFixture()
    {
        Tools = FfmpegLocator.Locate(null, Dir);
        if (Tools is null) return;
        var font = new[] { "/usr/share/fonts/truetype/dejavu/DejaVuSerif-Bold.ttf" }.FirstOrDefault(File.Exists);
        File.WriteAllText(Path.Combine(Dir, "s.ass"), """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1920
            PlayResY: 1080

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,DejaVu Serif,72,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,3,0,2,10,10,40,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:03.00,Default,,0,0,0,,Konjiki no Autoencoder!
            """.Replace("            ", ""));
        File.WriteAllText(Path.Combine(Dir, "s.srt"), "1\n00:00:00,000 --> 00:00:03,000\nHello\n");
        var args = new List<string>
        {
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1440x1080:rate=25:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-i", "s.ass", "-i", "s.srt",
        };
        if (font is not null) args.AddRange(["-attach", font, "-metadata:s:t:0", "mimetype=application/x-truetype-font"]);
        args.AddRange(["-map", "0", "-map", "1", "-map", "2", "-map", "3",
            "-vf", "setsar=4/3", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac", "-ac", "6", "-c:s", "copy",
            "-disposition:s:0", "default", "-disposition:a:0", "default", Sample]);
        var r = ProcessRunner.RunAsync(Tools.Ffmpeg, args, workingDirectory: Dir).GetAwaiter().GetResult();
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class IntegrationTests(SampleFixture fx) : IClassFixture<SampleFixture>
{
    private async Task<MediaInfo> Probe(string path) => await new MediaProber(fx.Tools!.Ffprobe).ProbeAsync(path);

    private async Task<QueueJob> RunJob(JobSettings settings, string? source = null)
    {
        var store = new SettingsStore(Path.Combine(fx.Dir, "data-" + Guid.NewGuid().ToString("N")));
        using var q = new QueueService(store);
        q.Settings.FfmpegPath = fx.Tools!.Ffmpeg;
        var job = Assert.Single(q.AddPaths([source ?? fx.Sample], settings));
        var done = new TaskCompletionSource();
        q.QueueChanged += () => { if (!q.IsRunning && job.Status != JobStatus.Ready && job.Status != JobStatus.Running) done.TrySetResult(); };
        q.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(job.Status is JobStatus.Done or JobStatus.Warning, job.Message + "\n" + job.Log);
        return job;
    }

    [Fact]
    public async Task Probe_reads_sample()
    {
        if (fx.Tools is null) return;
        var m = await Probe(fx.Sample);
        Assert.Equal(1440, m.PrimaryVideo!.Width);
        Assert.Equal(1920, m.PrimaryVideo.DisplayWidth, 1);
        Assert.Equal(6, m.Audio[0].Channels);
        Assert.Equal(2, m.Subtitles.Count);
        Assert.True(m.Attachments.Count(a => a.IsFont) <= 1);
    }

    [Fact]
    public async Task Hardsub_streaming_h264_360p()
    {
        if (fx.Tools is null) return;
        var job = await RunJob(new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Resolution = 360, Quality = QualityLevel.Draft, Subtitles = SubtitleMode.Hardsub });
        var m = await Probe(job.OutputPath!);
        Assert.EndsWith(".mp4", job.OutputPath);
        Assert.Equal(640, m.PrimaryVideo!.Width);
        Assert.Equal(360, m.PrimaryVideo.Height);
        Assert.Empty(m.Subtitles);
        Assert.Equal(2, Assert.Single(m.Audio).Channels);
        Assert.Contains("subtitles=filename=sub.ass", job.CommandLine);
        Assert.InRange(m.Duration.TotalSeconds, 2.8, 3.3);
        Assert.Equal(100, job.Percent);
    }

    [Fact]
    public async Task Softsub_universal_av1_10bit_keeps_tracks()
    {
        if (fx.Tools is null) return;
        var job = await RunJob(new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.AV1, TenBit = true, Resolution = 360, Quality = QualityLevel.Draft, Subtitles = SubtitleMode.Softsub });
        var m = await Probe(job.OutputPath!);
        Assert.Equal("av1", m.PrimaryVideo!.CodecName);
        Assert.Equal(10, m.PrimaryVideo.BitDepth);
        Assert.Equal(2, m.Subtitles.Count);
        Assert.Equal(6, m.Audio[0].Channels); // audio copied untouched
    }

    [Fact]
    public async Task Remux_mkv_and_mp4()
    {
        if (fx.Tools is null) return;
        var mkv = await RunJob(new JobSettings { Profile = ProfileKind.RemuxMkv });
        Assert.Equal(2, (await Probe(mkv.OutputPath!)).Subtitles.Count);
        var mp4 = await RunJob(new JobSettings { Profile = ProfileKind.RemuxMp4 });
        var m = await Probe(mp4.OutputPath!);
        Assert.Equal("h264", m.PrimaryVideo!.CodecName);
        Assert.All(m.Subtitles, s => Assert.Equal("mov_text", s.CodecName));
    }

    [Fact]
    public async Task Keyframes_are_written()
    {
        if (fx.Tools is null) return;
        var job = await RunJob(new JobSettings { Profile = ProfileKind.Keyframes });
        Assert.StartsWith("# keyframe format v1", await File.ReadAllTextAsync(job.OutputPath!));
    }

    [Fact]
    public async Task Pause_and_stop_work()
    {
        if (fx.Tools is null) return;
        var store = new SettingsStore(Path.Combine(fx.Dir, "data-stop"));
        using var q = new QueueService(store);
        var job = Assert.Single(q.AddPaths([fx.Sample], new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Quality = QualityLevel.VeryHigh, Resolution = 0 }));
        q.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (job.Stage != "Encoding" && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
        q.Pause();
        Assert.Equal(JobStatus.Paused, job.Status);
        await Task.Delay(300);
        q.Resume();
        await q.StopAsync();
        Assert.Equal(JobStatus.Stopped, job.Status);
        Assert.False(File.Exists(job.OutputPath ?? "nope"), "partial output must be deleted");
    }

    [Fact]
    public async Task Hardware_detection_reports_software_encoders()
    {
        if (fx.Tools is null) return;
        var caps = await HardwareDetector.DetectAsync(fx.Tools.Ffmpeg, null);
        Assert.True(caps.IsUsable(VideoCodec.H264, EncoderBackend.Software, false));
        Assert.True(caps.IsUsable(VideoCodec.AV1, EncoderBackend.Software, true));
        Assert.True(caps.HasLibass);
        // No GPU in CI: compiled-in NVENC must NOT be reported as usable.
        Assert.False(caps.IsUsable(VideoCodec.HEVC, EncoderBackend.Nvenc, false));
    }
}
