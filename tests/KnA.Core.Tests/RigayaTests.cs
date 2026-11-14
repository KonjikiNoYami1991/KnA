using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Processing;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;
using Xunit;

namespace KnA.Core.Tests;

public class RigayaArgsTests
{
    private static VideoStream Video(HdrInfo? hdr = null) => new()
    {
        Index = 0, Kind = "video", CodecName = "hevc", Width = 3840, Height = 2160, FrameRate = 23.976, BitDepth = 10, Hdr = hdr ?? HdrInfo.Sdr,
    };
    private static readonly VideoStream Uhd = Video();

    private static MediaInfo Media(VideoStream v) => new()
    {
        Path = "/m/film.mkv", Duration = TimeSpan.FromMinutes(100),
        Video = [v], Audio = [new AudioStream { Index = 1, Kind = "audio", CodecName = "ac3", Channels = 6, Language = "ita" }],
    };

    private static CompileContext Ctx(MediaInfo m, JobSettings s, JobEdit? edit = null) => new()
    {
        Source = m, Media = new Dictionary<string, MediaInfo> { [m.Path] = m }, Settings = s, Edit = edit ?? new(),
        Options = new BuildOptions { RigayaEncoders = new Dictionary<EncoderBackend, string> { [EncoderBackend.Nvenc] = "/t/NVEncC64" } },
    };

    private static readonly JobSettings Nvencc = new()
    {
        Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Backend = EncoderBackend.Nvenc, Resolution = 0, Subtitles = SubtitleMode.None,
        Hdr = HdrMode.Preserve, Encoder = new EncoderOverrides { Program = EncoderProgram.Rigaya },
    };

    [Fact]
    public void Encoder_options_follow_quality_and_hand_set_values()
    {
        var hdr = new HdrPlan();
        var a = RigayaEncoders.EncoderArgs(EncoderBackend.Nvenc, VideoCodec.HEVC, QualityLevel.Normal, true, new(), 24, hdr, Uhd, false);
        Assert.Equal(["-c", "hevc", "--qvbr", "25", "-u", "P4", "--tune", "hq", "--aq", "--lookahead", "32", "--output-depth", "10", "--profile", "main10"], a.Take(15));
        var b = RigayaEncoders.EncoderArgs(EncoderBackend.Qsv, VideoCodec.AV1, QualityLevel.VeryHigh, false,
            new EncoderOverrides { RateControl = RateControl.Bitrate, BitrateKbps = 9000, MaxBitrateKbps = 15000, KeyframeSeconds = 10, Extra = "--tune perceptual" }, 25, hdr, Uhd, false);
        Assert.Equal(["-c", "av1", "--vbr", "9000", "--max-bitrate", "15000", "-u", "best", "--output-depth", "8", "--gop-len", "250"], b.Take(12));
        Assert.Equal(["--tune", "perceptual"], b.TakeLast(2));
        var c = RigayaEncoders.EncoderArgs(EncoderBackend.Amf, VideoCodec.HEVC, QualityLevel.High, false, new(), 25, hdr, Uhd, false);
        Assert.Contains("20:22:24", c);   // HEVC on VCEEncC: CQP with I < P < B
    }

    [Fact]
    public void Plain_hdr_source_is_read_by_the_encoder_with_dolby_vision_copy()
    {
        var dv = Video(new HdrInfo { Format = HdrFormat.Hdr10, DolbyVisionProfile = 8, DolbyVisionCompatibility = 1, MaxCll = 1000, MaxFall = 400 });
        var job = CommandBuilder.Compile(Ctx(Media(dv), Nvencc with { Resolution = 1080 }));
        Assert.False(job.HasErrors, string.Join("\n", job.Issues.Select(i => i.Message)));
        var r = Assert.IsType<RigayaPlan>(job.Rigaya);
        var n = Assert.IsAssignableFrom<IReadOnlyList<string>>(r.NativeArgs);
        Assert.Equal(["--avhw", "-i", "/m/film.mkv", "--output-res", "1920x1080"], n.Take(5));
        Assert.Contains("--master-display", n);
        Assert.Equal("copy", n[n.ToList().IndexOf("--dolby-vision-rpu") + 1]);
        Assert.Equal("video.mkv", n[^1]);
    }

    [Fact]
    public void Filters_or_a_trim_send_y4m_frames_through_a_pipe()
    {
        var m = Media(Uhd);
        var job = CommandBuilder.Compile(Ctx(m, Nvencc with { VideoFilters = new VideoFilterSettings { Denoise = FilterStrength.Light } }));
        Assert.Null(job.Rigaya!.NativeArgs);
        Assert.Contains("filters", job.Rigaya.PipeReason);
        var trimmed = CommandBuilder.Compile(Ctx(m, Nvencc, new JobEdit { TrimStart = 10, TrimEnd = 20 }));
        Assert.Equal("trim", trimmed.Rigaya!.PipeReason);

        var y4m = CommandBuilder.Compile(Ctx(m, Nvencc) with { Chunk = new ChunkRequest(10, 20) { Y4m = true } });
        var a = y4m.Plan.Arguments.ToList();
        Assert.Equal(["-f", "yuv4mpegpipe", "pipe:1"], a.TakeLast(3));
        Assert.Equal("pipe:2", a[a.IndexOf("-progress") + 1]);
        Assert.Equal("yuv420p10le", a[a.IndexOf("-pix_fmt") + 1]);
        Assert.Contains("format=yuv420p10le", a[a.IndexOf("-filter_complex") + 1]);
        Assert.DoesNotContain("hevc_nvenc", a);
    }

    [Fact]
    public void Missing_encoder_is_an_error_and_ffmpeg_program_is_untouched()
    {
        var m = Media(Uhd);
        var missing = CommandBuilder.Compile(Ctx(m, Nvencc) with { Options = new BuildOptions() });
        Assert.Contains(missing.Issues, i => i.IsError && i.Message.Contains("NVEncC"));
        var ffmpeg = CommandBuilder.Compile(Ctx(m, Nvencc with { Encoder = new() }));
        Assert.Null(ffmpeg.Rigaya);
        Assert.Contains("hevc_nvenc", ffmpeg.Plan.Arguments);
    }

    [Fact]
    public void Status_lines_become_progress()
    {
        var p = RigayaEncoders.ParseProgress("[42.5%] 1234 frames: 245.67 fps, 5432 kb/s, remain 0:01:23, GPU 45%", TimeSpan.FromSeconds(100), 24);
        Assert.NotNull(p);
        Assert.Equal(42.5, p!.Position.TotalSeconds, 3);
        Assert.Equal(1234, p.Frame);
        Assert.Equal(245.67, p.Fps, 2);
        Assert.Null(RigayaEncoders.ParseProgress("NVEncC (x64) 8.0 (r3000) by rigaya", TimeSpan.Zero, 24));
    }
}

public class RigayaRunTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    /// <summary>A stand-in for NVEncC (no NVIDIA GPU here): FFmpeg encodes what the real encoder would read.</summary>
    private static void WriteFakeEncoder(string data, string ffmpeg, bool failNative)
    {
        var dir = Path.Combine(AppPaths.Tools(data), "nvencc");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nvencc");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            out=""; in=""; y4m=0; prev=""
            for a in "$@"; do
              case "$prev" in -o) out="$a";; -i) in="$a";; esac
              [ "$a" = "--y4m" ] && y4m=1
              prev="$a"
            done
            echo "[50.0%] 100 frames: 250.00 fps, 1000 kb/s, remain 0:00:01" >&2
            if [ $y4m = 1 ]; then exec "{{ffmpeg}}" -v error -y -f yuv4mpegpipe -i - -c:v libx264 -preset ultrafast "$out"; fi
            {{(failNative ? "echo 'avhw: unsupported' >&2; exit 3" : "")}}
            exec "{{ffmpeg}}" -v error -y -i "$in" -map 0:v:0 -c:v libx264 -preset ultrafast "$out"
            """.Replace("\r", ""));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Video_from_the_external_encoder_is_muxed_with_audio(bool failNative, bool trim)
    {
        if (fx.Tools is null || !OperatingSystem.IsLinux()) return;
        var src = Path.Combine(fx.Dir, "rigaya-src.mkv");
        if (!File.Exists(src))
        {
            var g = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=10",
                "-f", "lavfi", "-i", "sine=duration=10", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", src]);
            Assert.Equal(0, g.ExitCode);
        }
        var data = Path.Combine(fx.Dir, $"data-rigaya-{failNative}-{trim}");
        WriteFakeEncoder(data, fx.Tools.Ffmpeg, failNative);
        using var q = new QueueService(new SettingsStore(data), restoreQueue: false);
        q.Settings.HardwareDecoding = HardwareDecoding.Off;
        q.Settings.OutputDirectory = data;
        q.SaveSettings();
        var job = Assert.Single(q.AddPaths([src], new JobSettings
        {
            Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Backend = EncoderBackend.Nvenc, TenBit = false, Resolution = 0,
            Subtitles = SubtitleMode.None, Encoder = new EncoderOverrides { Program = EncoderProgram.Rigaya },
        }));
        if (trim) await q.SetEditAsync(job.Id, new JobEdit { TrimStart = 2, TrimEnd = 6 });
        var done = new TaskCompletionSource();
        q.QueueChanged += () => { if (!q.IsRunning) done.TrySetResult(); };
        q.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(job.Status is JobStatus.Done or JobStatus.Warning, job.Message + "\n" + job.Log);
        Assert.Contains(trim || failNative ? "FFmpeg pipe" : "GPU reader", job.Log);
        if (failNative) Assert.Contains("could not read the source itself", job.Log);
        var m = await new MediaProber(fx.Tools.Ffprobe).ProbeAsync(job.OutputPath!);
        Assert.Single(m.Audio);
        Assert.InRange(m.Duration.TotalSeconds, trim ? 3.8 : 9.8, trim ? 4.3 : 10.3);
        var frames = await ProcessRunner.RunAsync(fx.Tools.Ffprobe, ["-v", "error", "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", job.OutputPath!]);
        Assert.Equal(trim ? 100 : 250, int.Parse(frames.StdOut.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture));
    }
}

public class RigayaAssetTests
{
    [Fact]
    public void Deb_for_the_host_ubuntu_or_the_newest()
    {
        string[] names = ["NVEncC_8.05_x64.7z", "nvencc_8.05_Ubuntu22.04_amd64.deb", "nvencc_8.05_Ubuntu24.04_amd64.deb", "nvencc_8.05_Ubuntu24.04_arm64.deb"];
        Assert.Equal("nvencc_8.05_Ubuntu22.04_amd64.deb", ComponentManager.PickDeb(names, "nvencc", "22.04"));
        Assert.Equal("nvencc_8.05_Ubuntu24.04_amd64.deb", ComponentManager.PickDeb(names, "nvencc", null));
        Assert.Equal("qsvencc_7.9_amd64.deb", ComponentManager.PickDeb(["qsvencc_7.9_amd64.deb"], "qsvencc", "24.04"));
        Assert.Null(ComponentManager.PickDeb(names, "vceencc", null));
    }
}
