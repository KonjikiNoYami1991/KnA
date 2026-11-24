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
    public void Plain_hdr_source_is_read_by_the_encoder_with_dolby_vision_from_kna_rpu()
    {
        var dv = Video(new HdrInfo { Format = HdrFormat.Hdr10, DolbyVisionProfile = 8, DolbyVisionCompatibility = 1, MaxCll = 1000, MaxFall = 400 });
        var ctx = Ctx(Media(dv), Nvencc with { Resolution = 1080 }) with
        {
            Hdr = new HdrEnvironment(true, false, true, true),
            HdrFiles = new HdrSideFiles("/w/RPU.kept.bin", "/w/hdr10plus.json", "/w/RPU.bin"),
        };
        var job = CommandBuilder.Compile(ctx);
        Assert.False(job.HasErrors, string.Join("\n", job.Issues.Select(i => i.Message)));
        var r = Assert.IsType<RigayaPlan>(job.Rigaya);
        var n = Assert.IsAssignableFrom<IReadOnlyList<string>>(r.NativeArgs);
        Assert.Equal(["--avhw", "-i", "/m/film.mkv", "--output-res", "1920x1080", "--vpp-resize", "lanczos3"], n.Take(7));
        Assert.Contains("--master-display", n);
        // The encoder numbers the frames it reads itself: the uncut RPU; the FFmpeg pipe gets the frames already cut.
        Assert.Equal("/w/RPU.bin", n[n.ToList().IndexOf("--dolby-vision-rpu") + 1]);
        Assert.Equal("8.1", n[n.ToList().IndexOf("--dolby-vision-profile") + 1]);
        Assert.Equal("/w/RPU.kept.bin", r.EncoderArgs[r.EncoderArgs.ToList().IndexOf("--dolby-vision-rpu") + 1]);
        Assert.DoesNotContain("--parallel", n);
        Assert.Equal("video.mkv", n[^1]);
        Assert.Equal(HdrCarry.Encoder, job.Hdr.DolbyVision);
    }

    [Fact]
    public void Filters_and_trim_run_on_the_gpu_inside_the_encoder()
    {
        var m = Media(Uhd);
        var job = CommandBuilder.Compile(Ctx(m, Nvencc with
        {
            Resolution = 1080,
            VideoFilters = new VideoFilterSettings { Denoise = FilterStrength.Light, Deband = FilterStrength.Medium, Sharpen = FilterStrength.Light, Deinterlace = DeinterlaceMode.Force, Crop = new CropSettings(0, 140, 0, 140) },
        }));
        var n = job.Rigaya!.NativeArgs!.ToList();
        Assert.Null(job.Rigaya.PipeReason);
        Assert.Equal(["--avhw", "-i", "/m/film.mkv"], n.Take(3));
        Assert.Equal("luma_spatial=2,chroma_spatial=1.5,luma_temporal=3,chroma_temporal=2.25", n[n.IndexOf("--vpp-hqdn3d") + 1]);   // = FFmpeg's hqdn3d light
        Assert.Equal("range=15,thre=15,dither=15", n[n.IndexOf("--vpp-deband") + 1]);
        Assert.Equal("sharpness=0.3", n[n.IndexOf("--vpp-cas") + 1]);
        Assert.Equal("0,140,0,140", n[n.IndexOf("--crop") + 1]);
        Assert.Equal("1920x940", n[n.IndexOf("--output-res") + 1]);   // 3840×1880 after the crop
        Assert.Equal("lanczos3", n[n.IndexOf("--vpp-resize") + 1]);
        Assert.Contains("--vpp-bwdif", n);
        Assert.True(job.Gpu!.AllOnGpu);

        var trimmed = CommandBuilder.Compile(Ctx(m, Nvencc, new JobEdit { TrimStart = 10, TrimEnd = 20 }));
        var t = trimmed.Rigaya!.NativeArgs!.ToList();
        Assert.Equal("240:479", t[t.IndexOf("--trim") + 1]);   // frames 10 s × 23.976 … 20 s, exact
        Assert.Equal(10, trimmed.Rigaya.TrimStart);
        var toEnd = CommandBuilder.Compile(Ctx(m, Nvencc, new JobEdit { TrimStart = 10 })).Rigaya!.NativeArgs!.ToList();
        Assert.Equal("240:0", toEnd[toEnd.IndexOf("--trim") + 1]);

        // A variable frame rate can't be cut by frame number: FFmpeg reads and trims, and KnA says why.
        var vfr = new VideoStream { Index = 0, Kind = "video", CodecName = "h264", Width = 1920, Height = 1080, FrameRate = 29.97, VariableFrameRate = true };
        var piped = CommandBuilder.Compile(Ctx(Media(vfr), Nvencc with { Codec = VideoCodec.H264 }, new JobEdit { TrimStart = 10, TrimEnd = 20 }));
        Assert.Null(piped.Rigaya!.NativeArgs);
        Assert.Contains("variable", piped.Rigaya.PipeReason);
        Assert.Contains(piped.Gpu!.Steps, x => x.Place == StepPlace.CpuFfmpeg);
        Assert.Contains(piped.Issues, x => !x.IsError && x.Message.Contains("no GPU equivalent"));

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
        var ffmpeg = CommandBuilder.Compile(Ctx(m, Nvencc with { Encoder = new() { Program = EncoderProgram.FFmpeg } }));
        Assert.Null(ffmpeg.Rigaya);
        Assert.Contains("hevc_nvenc", ffmpeg.Plan.Arguments);
    }

    [Fact]
    public void Gpu_first_auto_picks_the_installed_rigaya_encoder_and_falls_back_to_ffmpeg()
    {
        var m = Media(Uhd);
        var auto = Nvencc with { Encoder = new() };
        Assert.Equal(EncoderProgram.Auto, auto.Encoder.Program);
        Assert.NotNull(CommandBuilder.Compile(Ctx(m, auto)).Rigaya);
        // Not installed: FFmpeg's GPU encoder, no error.
        var none = CommandBuilder.Compile(Ctx(m, auto) with { Options = new BuildOptions() });
        Assert.False(none.HasErrors);
        Assert.Null(none.Rigaya);
        Assert.Contains("hevc_nvenc", none.Plan.Arguments);
        Assert.Equal(EncoderProgram.Rigaya, RigayaEncoders.Resolve(auto, [EncoderBackend.Nvenc]));
        Assert.Equal(EncoderProgram.FFmpeg, RigayaEncoders.Resolve(auto, []));
        // H.264 on the CPU: never.
        Assert.False(RigayaEncoders.Wanted(auto with { Backend = EncoderBackend.Software }, [EncoderBackend.Nvenc]));
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
    internal static void WriteFakeEncoder(string data, string ffmpeg, bool failNative)
    {
        var dir = Path.Combine(AppPaths.Tools(data), "nvencc");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nvencc");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            out=""; in=""; y4m=0; prev=""; trim=""
            for a in "$@"; do
              case "$prev" in -o) out="$a";; -i) in="$a";; --trim) trim="$a";; esac
              [ "$a" = "--y4m" ] && y4m=1
              prev="$a"
            done
            echo "[50.0%] 100 frames: 250.00 fps, 1000 kb/s, remain 0:00:01" >&2
            if [ $y4m = 1 ]; then exec "{{ffmpeg}}" -v error -y -f yuv4mpegpipe -i - -c:v libx264 -preset ultrafast "$out"; fi
            {{(failNative ? "echo 'avhw: unsupported' >&2; exit 3" : "")}}
            vf="null"
            if [ -n "$trim" ]; then
              first="${trim%%:*}"; last="${trim##*:}"
              if [ "$last" = "0" ]; then vf="select=gte(n\\,$first),setpts=N/FRAME_RATE/TB"; else vf="select=between(n\\,$first\\,$last),setpts=N/FRAME_RATE/TB"; fi
            fi
            exec "{{ffmpeg}}" -v error -y -i "$in" -map 0:v:0 -vf "$vf" -c:v libx264 -preset ultrafast "$out"
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
        Assert.Contains(failNative ? "FFmpeg pipe" : "GPU reader", job.Log);   // a trim is done by the encoder too (--trim)
        if (trim && !failNative) Assert.Contains("--trim 50:149", job.Log);
        if (failNative) Assert.Contains("could not read the source itself", job.Log);
        var m = await new MediaProber(fx.Tools.Ffprobe).ProbeAsync(job.OutputPath!);
        Assert.Single(m.Audio);
        Assert.InRange(m.Duration.TotalSeconds, trim ? 3.8 : 9.8, trim ? 4.3 : 10.3);
        var frames = await ProcessRunner.RunAsync(fx.Tools.Ffprobe, ["-v", "error", "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", job.OutputPath!]);
        Assert.Equal(trim ? 100 : 250, int.Parse(frames.StdOut.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture));
    }
}

public class GpuBenchmarkTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    [Fact]
    public async Task The_mini_benchmark_measures_every_profile_and_estimates_the_job()
    {
        if (fx.Tools is null || !OperatingSystem.IsLinux()) return;
        var data = Path.Combine(fx.Dir, "data-bench");
        RigayaRunTests.WriteFakeEncoder(data, fx.Tools.Ffmpeg, failNative: false);
        using var q = new QueueService(new SettingsStore(data), restoreQueue: false);
        var r = await GpuBenchmark.RunAsync(q, EncoderBackend.Nvenc, VideoCodec.HEVC);
        Assert.Equal(["best", "balanced", "fast"], r.Select(x => x.Profile));
        Assert.All(r, x => Assert.True(x.Fps1080 > 0));
        Assert.Equal(3, q.Settings.GpuBenchmarks.Count);

        var source = new MediaInfo
        {
            Path = "/m/f.mkv", Duration = TimeSpan.FromMinutes(45),
            Video = [new VideoStream { Index = 0, Kind = "video", CodecName = "h264", Width = 3840, Height = 2160, FrameRate = 24, FrameCount = 64800 }],
        };
        var s = new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, Backend = EncoderBackend.Nvenc, Resolution = 1080, Encoder = new EncoderOverrides { Program = EncoderProgram.Rigaya } };
        var text = GpuBenchmark.EstimateFor(s, source, new JobEdit(), q.Settings.GpuBenchmarks);
        Assert.NotNull(text);
        Assert.Contains("Balanced", text);
        // A fixed set: 60 fps at 1080p → 45 min of 24 fps 4K → 1080p = 64800 frames / 60 = 18 min.
        var e = GpuBenchmark.Estimate([new GpuBenchmarkResult(EncoderBackend.Nvenc, VideoCodec.HEVC, "balanced", 60, DateTimeOffset.Now)],
            EncoderBackend.Nvenc, VideoCodec.HEVC, 1920, 1080, 24, TimeSpan.FromMinutes(45));
        Assert.Equal(18, Assert.Single(e).Time.TotalMinutes, 1);
        Assert.Equal("balanced", GpuBenchmark.Suggest(e, 24)!.Id);
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
