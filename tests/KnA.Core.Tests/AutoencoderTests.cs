using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Ocr;
using KnA.Core.Tools;
using Xunit;

namespace KnA.Core.Tests;

/// <summary>The autoencoder review: KnA finds out by itself (scan type, black bars, picture subtitles, the machine).</summary>
public class AutoencoderTests
{
    private static VideoStream Dvd(string? field = "tt", double fps = 29.97) => new()
    {
        Index = 0, Kind = "video", CodecName = "mpeg2video", Width = 720, Height = 480, FrameRate = fps, FieldOrder = field,
    };

    private static MediaInfo Media(VideoStream v, string path = "/m/dvd.mkv") => new()
    {
        Path = path, Duration = TimeSpan.FromMinutes(90), Video = [v],
        Audio = [new AudioStream { Index = 1, Kind = "audio", CodecName = "ac3", Channels = 2, Language = "ita" }],
        Subtitles = [new SubtitleStream { Index = 2, Kind = "subtitle", CodecName = "hdmv_pgs_subtitle", Language = "ita" }],
    };

    private static CompileContext Ctx(MediaInfo m, JobSettings s) => new()
    {
        Source = m, Media = new Dictionary<string, MediaInfo> { [m.Path] = m }, Settings = s,
    };

    private static readonly JobSettings X264 = new()
    {
        Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Backend = EncoderBackend.Software, Resolution = 0, Subtitles = SubtitleMode.None,
    };

    private static string Graph(CompiledJob j) => j.Plan.Arguments[j.Plan.Arguments.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void Idet_summary_is_parsed_and_classified()
    {
        const string log = """
            [Parsed_idet_0 @ 0x1] Repeated Fields: Neither:   480 Top:     3 Bottom:     2
            [Parsed_idet_0 @ 0x1] Single frame detection: TFF:   150 BFF:     0 Progressive:   300 Undetermined:    35
            [Parsed_idet_0 @ 0x1] Multi frame detection: TFF:   190 BFF:     0 Progressive:   290 Undetermined:     5
            """;
        var c = ScanDetector.Parse(log);
        Assert.Equal(190, c.Tff);
        Assert.Equal(290, c.Progressive);
        Assert.Equal(480, c.Neither);
        // ~40% combed at 29.97: the 3:2 pattern → telecine.
        Assert.Equal(ScanType.Telecine, ScanDetector.Classify(c, 29.97));
        Assert.Equal(ScanType.Interlaced, ScanDetector.Classify(new ScanDetector.IdetCounts(Tff: 470, Progressive: 10), 29.97));
        Assert.Equal(ScanType.Interlaced, ScanDetector.Classify(new ScanDetector.IdetCounts(Tff: 200, Progressive: 300), 25));
        Assert.Equal(ScanType.Progressive, ScanDetector.Classify(new ScanDetector.IdetCounts(Tff: 3, Progressive: 480), 29.97));
        // What FFmpeg's idet really prints for 3:2 pulldown (every frame combed by the synthetic pattern, 40% of the
        // fields repeated) and for true interlaced video.
        Assert.Equal(ScanType.Telecine, ScanDetector.Classify(new ScanDetector.IdetCounts(Tff: 300, Neither: 180, Top: 60, Bottom: 60), 29.97));
        Assert.Equal(ScanType.Interlaced, ScanDetector.Classify(new ScanDetector.IdetCounts(Tff: 150, Neither: 150), 29.97));
        Assert.Null(ScanDetector.Classify(new ScanDetector.IdetCounts(Progressive: 10), 29.97));
    }

    [Fact]
    public void Automatic_deinterlace_follows_the_analysis_not_the_flag()
    {
        var telecine = Dvd();
        telecine.Scan = ScanType.Telecine;
        var j = CommandBuilder.Compile(Ctx(Media(telecine), X264));
        Assert.Contains(FilterBuilder.IvtcFilter, Graph(j));
        // A progressive file with a wrong interlaced flag: nothing.
        var wrongFlag = Dvd();
        wrongFlag.Scan = ScanType.Progressive;
        Assert.DoesNotContain("bwdif", Graph(CommandBuilder.Compile(Ctx(Media(wrongFlag), X264))));
        // Not analysed: the flag decides, as before.
        Assert.Contains("bwdif=mode=send_frame:parity=auto:deint=interlaced", Graph(CommandBuilder.Compile(Ctx(Media(Dvd()), X264))));
        // The user can still choose.
        Assert.DoesNotContain("fieldmatch", Graph(CommandBuilder.Compile(Ctx(Media(telecine), X264 with { VideoFilters = new() { Deinterlace = DeinterlaceMode.Off } }))));
        Assert.Contains("fieldmatch", Graph(CommandBuilder.Compile(Ctx(Media(Dvd()), X264 with { VideoFilters = new() { Deinterlace = DeinterlaceMode.Ivtc } }))));
    }

    [Fact]
    public void Telecine_on_the_gpu_uses_the_encoders_ivtc()
    {
        var v = Dvd();
        v.Scan = ScanType.Telecine;
        var t = RigayaFilters.Translate(new RigayaFilters.Input
        {
            Backend = EncoderBackend.Nvenc, Video = v, Settings = X264, Hdr = new HdrPlan(),
        });
        Assert.Contains("--vpp-ivtc", t.Args);
        Assert.DoesNotContain("--vpp-bwdif", t.Args);
    }

    [Fact]
    public void Cropdetect_lines_become_constant_bars_only()
    {
        Assert.Equal(new CropSettings(0, 138, 0, 138), CropDetector.Parse("[Parsed_cropdetect_0] x1:0 ... crop=1920:804:0:138", 1920, 1080));
        Assert.Null(CropDetector.Parse("crop=-1904:-1072:1912:1076", 1920, 1080));   // black frame
        // Variable bars (a 16:9 scene in a scope film, an unreadable sample): only what every sample has.
        var bars = CropDetector.Combine([new CropSettings(0, 138, 0, 138), new CropSettings(0, 140, 0, 136), new CropSettings(0, 0, 0, 0), null], 1920, 1080);
        Assert.Null(bars);
        var scope = CropDetector.Combine([new CropSettings(0, 138, 0, 138), new CropSettings(0, 140, 0, 139), new CropSettings(2, 137, 0, 138)], 1920, 1080);
        Assert.Equal(new CropSettings(0, 136, 0, 138), scope);   // thin side bars ignored, even values
        Assert.Null(CropDetector.Combine([null, null, new CropSettings(0, 138, 0, 138)], 1920, 1080));   // too few samples
    }

    [Fact]
    public void Black_bars_found_are_cut_by_themselves_unless_switched_off()
    {
        var v = Dvd(null, 23.976);
        v.DetectedCrop = new CropSettings(0, 60, 0, 60);
        var j = CommandBuilder.Compile(Ctx(Media(v), X264));
        Assert.Contains("crop=", Graph(j));
        Assert.DoesNotContain("crop=", Graph(CommandBuilder.Compile(Ctx(Media(v), X264 with { VideoFilters = new() { AutoCrop = false } }))));
        // A crop chosen by hand (also "none") wins.
        Assert.DoesNotContain("crop=", Graph(CommandBuilder.Compile(Ctx(Media(v), X264 with { VideoFilters = new() { Crop = new CropSettings() } }))));
    }

    [Fact]
    public void Picture_subtitles_the_container_cannot_hold_are_read_with_ocr()
    {
        var m = Media(Dvd(null, 23.976));
        var mp4 = X264 with { Container = "mp4", Subtitles = SubtitleMode.Softsub };
        var j = CommandBuilder.Compile(Ctx(m, mp4));
        Assert.Contains(j.Issues, i => i.Message.Contains("OCR"));
        var track = j.Tracks.First(t => t.Kind == "subtitle");
        Assert.True(SubtitleOcr.Wanted(track, mp4, m.Subtitles[0], "mp4"));
        // MKV holds PGS: copied as it is.
        Assert.False(SubtitleOcr.Wanted(track, X264 with { Subtitles = SubtitleMode.Softsub }, m.Subtitles[0], "mkv"));
    }

    [Fact]
    public void Machine_fingerprint_follows_the_encoder_programs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kna-fp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var ff = Path.Combine(dir, "ffmpeg");
            File.WriteAllText(ff, "a");
            var tools = new FfmpegTools(ff, ff, null);
            var a = MachineFingerprint.Compute(tools);
            Assert.Equal(a, MachineFingerprint.Compute(tools));
            File.WriteAllText(ff, "a newer build");
            Assert.NotEqual(a, MachineFingerprint.Compute(tools));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Real_ffmpeg_finds_letterbox_bars_and_telecine()
    {
        var dir = Directory.CreateTempSubdirectory("kna-auto-").FullName;
        try
        {
            if (FfmpegLocator.Locate(null, dir) is not { } tools) return;   // no FFmpeg here: nothing to check
            var lb = Path.Combine(dir, "letterbox.mkv");
            var tc = Path.Combine(dir, "telecine.mpg");
            await Processing.ProcessRunner.RunAsync(tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1920x804:rate=24000/1001:duration=20",
                "-vf", "pad=1920:1080:0:138:black", "-c:v", "libx264", "-preset", "ultrafast", "-g", "48", "-pix_fmt", "yuv420p", lb]);
            await Processing.ProcessRunner.RunAsync(tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=720x480:rate=24000/1001:duration=20",
                "-vf", "telecine=pattern=23,setfield=tff", "-flags", "+ildct+ilme", "-c:v", "mpeg2video", "-b:v", "6M", tc]);
            if (!File.Exists(lb) || !File.Exists(tc)) return;
            var prober = MediaProber.For(tools);
            var m = await prober.ProbeAsync(lb);
            await CropDetector.ApplyAsync(tools.Ffmpeg, m);
            Assert.Equal(new CropSettings(0, 138, 0, 138), m.PrimaryVideo!.DetectedCrop);
            var t = await prober.ProbeAsync(tc);
            Assert.True(ScanDetector.Worth(t.PrimaryVideo!));
            await ScanDetector.ApplyAsync(tools.Ffmpeg, t);
            Assert.Equal(ScanType.Telecine, t.PrimaryVideo!.Scan);
        }
        finally { Directory.Delete(dir, true); }
    }
}
