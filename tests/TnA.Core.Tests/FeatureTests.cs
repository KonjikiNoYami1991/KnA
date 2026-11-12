using System.Globalization;
using TnA.Core.Encoding;
using TnA.Core.Media;
using TnA.Core.Processing;
using TnA.Core.Queue;
using TnA.Core.Settings;
using TnA.Core.Tools;

namespace TnA.Core.Tests;

/// <summary>
/// Fixtures for the advanced features: a black 20 s video (so burned white text is easy to detect),
/// an external ASS with an event at 10–12 s, an external FLAC, a font, an HDR10 HEVC clip and a TS file.
/// </summary>
public sealed class FeatureFixture : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("tna-ft-").FullName;
    public FfmpegTools? Tools { get; }
    public string Black => Path.Combine(Dir, "Black Ep 01.mkv");
    public string ExtSubs => Path.Combine(Dir, "external.ass");
    public string ExtAudio => Path.Combine(Dir, "commentary.flac");
    public string Font => Path.Combine(Dir, "MyFont.ttf");
    public string Hdr => Path.Combine(Dir, "HDR Sample.mkv");
    public string Ts => Path.Combine(Dir, "Broadcast.ts");

    public FeatureFixture()
    {
        Tools = FfmpegLocator.Locate(null, Dir);
        if (Tools is null) return;
        var font = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf";
        if (File.Exists(font)) File.Copy(font, Font);
        File.WriteAllText(ExtSubs, """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 640
            PlayResY: 360

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,DejaVu Sans,90,&H00FFFFFF,&H000000FF,&H00FFFFFF,&H00FFFFFF,-1,0,0,0,100,100,0,0,3,20,0,2,10,10,20,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:10.00,0:00:12.00,Default,,0,0,0,,WWWWWWWW
            """.Replace("            ", ""));
        Run("-f", "lavfi", "-i", "color=black:size=640x360:rate=25:duration=20", "-f", "lavfi", "-i", "sine=frequency=300:duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", Black);
        Run("-f", "lavfi", "-i", "sine=frequency=800:duration=20", "-c:a", "flac", ExtAudio);
        Run("-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=2", "-c:v", "libx265", "-pix_fmt", "yuv420p10le",
            "-x265-params", "log-level=error:hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", Hdr);
        Run("-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=3", "-f", "lavfi", "-i", "sine=duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "mp2", "-f", "mpegts", Ts);
    }

    private void Run(params string[] args)
    {
        var r = ProcessRunner.RunAsync(Tools!.Ffmpeg, ["-hide_banner", "-v", "error", "-y", .. args], workingDirectory: Dir).GetAwaiter().GetResult();
        if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class FeatureTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    private QueueService NewQueue(string name) => new(new SettingsStore(Path.Combine(fx.Dir, "data-" + name)), restoreQueue: false);

    private static async Task Finish(QueueService q, QueueJob job)
    {
        var done = new TaskCompletionSource();
        q.QueueChanged += () => { if (!q.IsRunning) done.TrySetResult(); };
        q.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(4));
        Assert.True(job.Status is JobStatus.Done or JobStatus.Warning, job.Message + "\n" + job.Log);
    }

    private async Task<MediaInfo> Probe(string path) => await new MediaProber(fx.Tools!.Ffprobe).ProbeAsync(path, requireVideo: false);

    /// <summary>Average luma of the lower third at time t (black ≈ 16, with the white box burned ≫ 30).</summary>
    private async Task<double> BottomLuma(string file, double t)
    {
        var r = await ProcessRunner.RunAsync(fx.Tools!.Ffmpeg, ["-hide_banner", "-ss", t.ToString(CultureInfo.InvariantCulture), "-i", file,
            "-frames:v", "1", "-vf", "crop=iw:ih/3:0:2*ih/3,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-"]);
        var line = r.StdOut.Split('\n').First(l => l.Contains("YAVG"));
        return double.Parse(line.Split('=')[1], CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Trim_is_applied_after_imported_and_delayed_subtitles()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("trim");
        var job = Assert.Single(q.AddPaths([fx.Black], new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 0 }));
        var a = await q.AnalyzeAsync(job);
        // Burn the external subtitle (event 10–12 s) delayed by +3 s → visible 13–15 s on the original timeline.
        var tracks = a.Tracks.Where(t => t.Kind != "subtitle").ToList();
        tracks.Add(new TrackPlan { File = fx.ExtSubs, Stream = 0, Kind = "subtitle", Include = false, Burn = true, DelayMs = 3000 });
        tracks.Add(new TrackPlan { File = fx.Font, Stream = -1, Kind = "attachment", Include = false });
        // Trim 12 → 16: the subtitle must appear between 1 s and 3 s of the OUTPUT.
        await q.SetEditAsync(job.Id, new JobEdit { ExternalFiles = [fx.ExtSubs, fx.Font], Tracks = tracks, TrimStart = 12, TrimEnd = 16 });
        await Finish(q, job);

        var m = await Probe(job.OutputPath!);
        Assert.InRange(m.Duration.TotalSeconds, 3.8, 4.3);
        Assert.True(await BottomLuma(job.OutputPath!, 0.4) < 25, "no subtitle before 13 s");
        Assert.True(await BottomLuma(job.OutputPath!, 2.0) > 40, "subtitle visible at 14 s");
        Assert.True(await BottomLuma(job.OutputPath!, 3.6) < 25, "subtitle gone after 15 s");
    }

    [Fact]
    public async Task External_audio_with_delay_replaces_original_and_fonts_force_mkv()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("ext");
        var job = Assert.Single(q.AddPaths([fx.Black], new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 360, Container = "mp4", Subtitles = SubtitleMode.None }));
        await q.AnalyzeAsync(job);
        var tracks = new List<TrackPlan>
        {
            new() { File = fx.Black, Stream = 0, Kind = "video" },
            new() { File = fx.Black, Stream = 1, Kind = "audio", Include = false },                     // removed
            new() { File = fx.ExtAudio, Stream = 0, Kind = "audio", DelayMs = 2000, Language = "ita", Title = "Commento", Default = true },
            new() { File = fx.ExtSubs, Stream = 0, Kind = "subtitle", Language = "ita" },
            new() { File = fx.Font, Stream = -1, Kind = "attachment" },
        };
        var a = await q.SetEditAsync(job.Id, new JobEdit { ExternalFiles = [fx.ExtAudio, fx.ExtSubs, fx.Font], Tracks = tracks });
        Assert.Equal("mkv", a.Container);
        Assert.Contains(a.Issues, i => !i.IsError && i.Message.Contains("Matroska"));
        await Finish(q, job);

        var m = await Probe(job.OutputPath!);
        Assert.EndsWith(".mkv", job.OutputPath);
        var audio = Assert.Single(m.Audio);
        Assert.Equal("flac", audio.CodecName); // lossless external copied
        Assert.Equal("ita", audio.Language);
        Assert.Equal("Commento", audio.Title);
        Assert.Single(m.Subtitles);
        Assert.Single(m.Attachments);
        var start = await ProcessRunner.RunAsync(fx.Tools.Ffprobe, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", job.OutputPath!]);
        Assert.InRange(double.Parse(start.StdOut.Trim(), CultureInfo.InvariantCulture), 1.9, 2.1);
    }

    [Fact]
    public void Container_rules_are_enforced()
    {
        var media = FfprobeParser.Parse("x.mkv", """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":640,"height":360,"avg_frame_rate":"25/1"},{"index":1,"codec_type":"subtitle","codec_name":"hdmv_pgs_subtitle"}],"format":{"duration":"10"}}""");
        var webmH264 = CommandBuilder.Compile(new CompileContext { Source = media, Settings = new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Container = "webm" } });
        Assert.Contains(webmH264.Issues, i => i.IsError);
        var webmAv1 = CommandBuilder.Compile(new CompileContext { Source = media, Settings = new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.AV1, Container = "webm" } });
        Assert.DoesNotContain(webmAv1.Issues, i => i.IsError);
        Assert.DoesNotContain("avi", Containers.Allowed(new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC }));
        // PGS in MP4 is skipped with a warning.
        var mp4Pgs = CommandBuilder.Compile(new CompileContext { Source = media, Settings = new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Container = "mp4", Subtitles = SubtitleMode.Softsub } });
        Assert.Contains(mp4Pgs.Issues, i => !i.IsError && i.Message.Contains("hdmv_pgs_subtitle"));
    }

    [Fact]
    public void Transport_stream_data_streams_are_ignored_and_inputs_hardened()
    {
        var media = FfprobeParser.Parse("rec.ts", """
            {"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"25/1"},
              {"index":1,"codec_type":"audio","codec_name":"mp2","channels":2},
              {"index":2,"codec_type":"data","codec_name":"scte_35"},
              {"index":3,"codec_type":"subtitle","codec_name":"dvb_teletext"},
              {"index":4,"codec_type":"data","codec_name":"bin_data"}],
             "format":{"duration":"60","format_name":"mpegts"}}
            """);
        Assert.Equal(3, media.Ignored.Count);
        var c = CommandBuilder.Compile(new CompileContext { Source = media, Settings = new JobSettings { Profile = ProfileKind.RemuxMkv } });
        var args = c.Plan.Arguments;
        Assert.Contains("+genpts+discardcorrupt", args);
        Assert.Contains("-ignore_unknown", args);
        Assert.DoesNotContain("0:2", args);
        Assert.DoesNotContain("0:4", args);
        Assert.Equal("-1", args[args.ToList().IndexOf("-map_metadata") + 1]);
    }

    [Fact]
    public async Task Real_ts_remux_works()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("ts");
        var job = Assert.Single(q.AddPaths([fx.Ts], new JobSettings { Profile = ProfileKind.RemuxMkv }));
        await Finish(q, job);
        Assert.True((await Probe(job.OutputPath!)).Audio.Count == 1);
    }

    [Fact]
    public async Task Hdr10_is_detected_preserved_and_tonemapped()
    {
        if (fx.Tools is null) return;
        var m = await Probe(fx.Hdr);
        var v = m.PrimaryVideo!;
        Assert.Equal(HdrFormat.Hdr10, v.Hdr.Format);
        Assert.Equal(1000, v.Hdr.MaxCll);
        Assert.NotNull(v.Hdr.Mastering);
        Assert.Equal("G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)", v.Hdr.Mastering!.ToX265());

        using var q = NewQueue("hdr");
        var keep = Assert.Single(q.AddPaths([fx.Hdr], new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.HEVC, TenBit = true, Quality = QualityLevel.Draft, Resolution = 0 }));
        var sdr = Assert.Single(q.AddPaths([fx.Hdr], new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 0 }));
        await Finish(q, keep);
        Assert.True(sdr.Status is JobStatus.Done or JobStatus.Warning, sdr.Message + sdr.Log);

        var kept = (await Probe(keep.OutputPath!)).PrimaryVideo!;
        Assert.Equal("smpte2084", kept.ColorTransfer);
        Assert.Equal(1000, kept.Hdr.MaxCll);
        Assert.Contains("master-display=", keep.CommandLine);
        var toned = (await Probe(sdr.OutputPath!)).PrimaryVideo!;
        Assert.Equal("bt709", toned.ColorTransfer);
        Assert.Contains("tonemap", sdr.CommandLine);
    }

    [Fact]
    public void Dolby_vision_profile5_without_tools_falls_back_to_sdr()
    {
        var v = new VideoStream { Width = 3840, Height = 2160, ColorTransfer = "smpte2084", Hdr = new HdrInfo { Format = HdrFormat.DolbyVision, DolbyVisionProfile = 5, DolbyVisionCompatibility = 0 } };
        var plan = HdrPlanner.Plan(v, new JobSettings { Codec = VideoCodec.HEVC, TenBit = true }, false, false, HdrEnvironment.Unknown);
        Assert.Equal(HdrAction.ToneMap, plan.Action);
        Assert.Contains(plan.Issues, i => i.Message.Contains("libplacebo") || i.Message.Contains("profile 5"));
        var p8 = HdrPlanner.Plan(v, new JobSettings { Codec = VideoCodec.HEVC, TenBit = true }, false, false, new HdrEnvironment(true, false, true, true));
        Assert.Equal(HdrAction.ToneMap, p8.Action); // still profile 5
        var v81 = new VideoStream { Width = 3840, Height = 2160, ColorTransfer = "smpte2084", Hdr = new HdrInfo { Format = HdrFormat.DolbyVision, DolbyVisionProfile = 8, DolbyVisionCompatibility = 1 } };
        var keep = HdrPlanner.Plan(v81, new JobSettings { Codec = VideoCodec.HEVC, TenBit = true }, false, false, new HdrEnvironment(true, false, true, false));
        Assert.True(keep.UseDolbyVisionRpu);
        var trimmed = HdrPlanner.Plan(v81, new JobSettings { Codec = VideoCodec.HEVC, TenBit = true }, false, true, new HdrEnvironment(true, false, true, false));
        Assert.False(trimmed.UseDolbyVisionRpu);
        Assert.Equal(HdrAction.Preserve, trimmed.Action);
    }

    [Fact]
    public async Task Video_and_audio_filters_are_applied()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("filters");
        var s = new JobSettings
        {
            Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 0, Subtitles = SubtitleMode.None,
            VideoFilters = new VideoFilterSettings { Crop = new CropSettings(20, 10, 20, 10), Denoise = FilterStrength.Light, Deband = FilterStrength.Light, Orientation = 90, Fps = 12.5 },
            AudioFilters = new AudioFilterSettings { GainDb = -3, DownmixStereo = true, SampleRate = 44100 },
        };
        var job = Assert.Single(q.AddPaths([fx.Black], s));
        await Finish(q, job);
        var m = await Probe(job.OutputPath!);
        Assert.Equal(320, m.PrimaryVideo!.Width);   // rotated first (360×640), then cropped: (360-40) × (640-20)
        Assert.Equal(620, m.PrimaryVideo.Height);
        Assert.Equal(0, m.PrimaryVideo.Rotation);   // baked in, no leftover matrix
        Assert.Equal(12.5, m.PrimaryVideo.FrameRate, 1);
        Assert.Equal(44100, m.Audio[0].SampleRate);
        Assert.Contains("volume=-3dB", job.CommandLine);
    }

    [Fact]
    public async Task Demux_extracts_streams_attachments_and_chapters()
    {
        if (fx.Tools is null) return;
        var src = Path.Combine(fx.Dir, "ToDemux.mkv");
        await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-v", "error", "-y", "-i", fx.Black, "-i", fx.ExtSubs, "-attach", fx.Font,
            "-metadata:s:t:0", "mimetype=application/x-truetype-font", "-map", "0", "-map", "1", "-c", "copy", "-t", "3", src]);
        using var q = NewQueue("demux");
        var job = Assert.Single(q.AddPaths([src], new JobSettings { Profile = ProfileKind.Demux }));
        await Finish(q, job);
        var files = Directory.GetFiles(job.OutputPath!).Select(Path.GetFileName).ToList();
        Assert.Contains(files, f => f!.EndsWith(".h264"));
        Assert.Contains(files, f => f!.EndsWith(".aac"));
        Assert.Contains(files, f => f!.EndsWith(".ass"));
        Assert.Contains("MyFont.ttf", files);
    }

    [Fact]
    public async Task Images_export_respects_count_and_format()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("img");
        var job = Assert.Single(q.AddPaths([fx.Black], new JobSettings
        {
            Profile = ProfileKind.Images, Resolution = 0, Subtitles = SubtitleMode.None,
            Images = new ImageSettings { Format = ImageFormat.Jpeg, Mode = ImageMode.Interval, IntervalSeconds = 5 },
        }));
        await Finish(q, job);
        var files = Directory.GetFiles(job.OutputPath!, "*.jpg");
        Assert.InRange(files.Length, 4, 5);
    }

    [Fact]
    public async Task Preview_frame_shows_burned_subtitles_and_waveform_renders()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("preview");
        var job = Assert.Single(q.AddPaths([fx.Black], new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.H264 }));
        await q.SetEditAsync(job.Id, new JobEdit
        {
            ExternalFiles = [fx.ExtSubs],
            Tracks = [new() { File = fx.Black, Stream = 0, Kind = "video" }, new() { File = fx.ExtSubs, Stream = 0, Kind = "subtitle", Include = false, Burn = true }],
        });
        var final = await q.Preview.FrameAsync(job.Id, 11, final: true);
        var original = await q.Preview.FrameAsync(job.Id, 11, final: false);
        Assert.True(await BottomLuma(final, 0) > 40, "subtitles in the final preview");
        Assert.True(await BottomLuma(original, 0) < 25, "no subtitles in the original");
        var wave = await q.Preview.WaveformAsync(job.Id, fx.Black, 1, 800, 80);
        var wm = await Probe(wave);
        Assert.Equal(800, wm.PrimaryVideo!.Width);
        Assert.Equal(80, wm.PrimaryVideo.Height);
        var clip = await q.Preview.ClipAsync(job.Id, 10, 3);
        Assert.InRange((await Probe(clip)).Duration.TotalSeconds, 2.5, 3.5);
    }

    [Fact]
    public void Wizard_builds_sensible_settings()
    {
        var caps = new FfmpegCapabilities("x", [new EncoderCapability(VideoCodec.AV1, EncoderBackend.Nvenc, "av1_nvenc", true, true, true, null)], true, DateTimeOffset.Now);
        var s = PresetWizard.Build(new WizardAnswers(WizardGoal.Archive, WizardCodec.Smallest, WizardSpeed.Fast), caps);
        Assert.Equal(VideoCodec.AV1, s.Codec);
        Assert.Equal(EncoderBackend.Nvenc, s.Backend);
        var phone = PresetWizard.Build(new WizardAnswers(WizardGoal.Phone, WizardCodec.Smallest, Resolution: 1080, Audio: WizardAudio.Stereo), caps);
        Assert.Equal(VideoCodec.H264, phone.Codec);
        Assert.Equal(720, phone.Resolution);
        Assert.True(phone.AudioFilters.DownmixStereo);

        var store = new PresetStore(Directory.CreateTempSubdirectory().FullName);
        store.Save(new Preset("Mio preset", null, s));
        Assert.Equal(EncoderBackend.Nvenc, store.Get("mio preset")!.Settings.Backend);
        Assert.Throws<InvalidOperationException>(() => store.Save(new Preset(PresetStore.BuiltIns[0].Name, null, s)));
    }

    [Fact]
    public void Queue_survives_a_restart()
    {
        if (fx.Tools is null) return;
        var data = Path.Combine(fx.Dir, "data-persist");
        using (var q = new QueueService(new SettingsStore(data)))
        {
            var j = Assert.Single(q.AddPaths([fx.Black], new JobSettings { Profile = ProfileKind.Images }));
            q.SetEditAsync(j.Id, new JobEdit { TrimStart = 5 }).GetAwaiter().GetResult();
        }
        using var q2 = new QueueService(new SettingsStore(data));
        var restored = Assert.Single(q2.Jobs);
        Assert.Equal(ProfileKind.Images, restored.Settings.Profile);
        Assert.Equal(5, restored.Edit.TrimStart);
    }

    [Fact]
    public async Task Rotated_phone_video_is_detected_and_fixed_losslessly_or_by_encoding()
    {
        if (fx.Tools is null) return;
        var phone = Path.Combine(fx.Dir, "phone.mp4");
        await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", phone]);
        // -display_rotation is an input option: re-mux to bake the matrix into the file.
        var tagged = Path.Combine(fx.Dir, "phone-tagged.mp4");
        await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-v", "error", "-y", "-display_rotation:v:0", "-90", "-i", phone, "-c", "copy", tagged]);
        var m = await Probe(tagged);
        Assert.Equal(90, m.PrimaryVideo!.Rotation);

        using var q = NewQueue("rot");
        // 1) encode with automatic orientation → portrait frames, resize uses the rotated size.
        var auto = Assert.Single(q.AddPaths([tagged], new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 288, Subtitles = SubtitleMode.None }));
        // 2) remux correcting the (wrong) flag to 0° without re-encoding.
        var fix = Assert.Single(q.AddPaths([tagged], new JobSettings { Profile = ProfileKind.RemuxMkv, VideoFilters = new() { Orientation = 0 } }));
        await Finish(q, auto);
        Assert.True(fix.Status is JobStatus.Done or JobStatus.Warning, fix.Log);
        var a = (await Probe(auto.OutputPath!)).PrimaryVideo!;
        Assert.Equal(288, a.Height);   // portrait: height bounded (288p target)
        Assert.True(a.Width < a.Height);
        var f = (await Probe(fix.OutputPath!)).PrimaryVideo!;
        Assert.Equal(0, f.Rotation);
        Assert.Equal(640, f.Width);
    }

    [Fact]
    public void Release_versions_parse()
    {
        Assert.Equal(new Version(2, 1, 0), ReleaseInfo.ParseVersion("v2.1.0"));
        Assert.Equal(new Version(2, 1), ReleaseInfo.ParseVersion("2.1-beta"));
        Assert.Null(ReleaseInfo.ParseVersion("latest"));
        var r = GitHubReleases.Parse("""{"tag_name":"2.3.4","name":"x","published_at":"2026-09-10T00:00:00Z","html_url":"u","assets":[{"name":"dovi_tool-2.3.4-x86_64-unknown-linux-musl.tar.gz","browser_download_url":"https://x/y","size":5}]}""");
        Assert.EndsWith("musl.tar.gz", r.Assets[0].Name);
    }
}
