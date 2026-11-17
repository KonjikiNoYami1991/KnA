using KnA.Core.Media;
using KnA.Core.Ocr;
using KnA.Core.Processing;
using KnA.Core.Tools;
using Xunit;

namespace KnA.Core.Tests;

public class OcrTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    private async Task<PgsWriter.Picture> TextPicture(string text, double start, double end, string font = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf")
    {
        var raw = Path.Combine(fx.Dir, "txt-" + Guid.NewGuid().ToString("N")[..6] + ".gray");
        var r = await ProcessRunner.RunAsync(fx.Tools!.Ffmpeg, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=900x80",
            "-vf", $"drawtext=fontfile={font}:text='{text}':fontcolor=white:fontsize=46:x=20:y=14:borderw=3:bordercolor=0x101010",
            "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", raw]);
        Assert.Equal(0, r.ExitCode);
        var g = File.ReadAllBytes(raw);
        var idx = g.Select(v => v > 200 ? (byte)1 : v > 90 ? (byte)3 : v > 8 ? (byte)2 : (byte)0).ToArray();
        return new PgsWriter.Picture(start, end, 900, 80, idx);
    }

    [Fact]
    public void Pgs_pictures_are_decoded_with_their_timing()
    {
        var px = new byte[20 * 4];
        for (int i = 0; i < 20; i++) px[20 + i] = 1;   // a white line on row 1
        var sup = PgsWriter.Write([new PgsWriter.Picture(1.5, 3.25, 20, 4, px), new PgsWriter.Picture(5, 6, 20, 4, px)]);
        var images = PgsParser.Parse(sup);
        Assert.Equal(2, images.Count);
        Assert.Equal(1.5, images[0].Start.TotalSeconds, 2);
        Assert.Equal(3.25, images[0].End.TotalSeconds, 2);
        Assert.Equal(20, images[0].Width);
        Assert.True(images[0].Rgba[(20 + 5) * 4 + 3] == 255 && images[0].Rgba[(20 + 5) * 4] > 230);   // opaque white
        Assert.Equal(0, images[0].Rgba[5 * 4 + 3]);                                                 // transparent
    }

    [Fact]
    public async Task Pgs_track_becomes_srt_with_tesseract()
    {
        if (fx.Tools is null) return;
        var data = Path.Combine(fx.Dir, "data-ocr");
        var tesseract = SubtitleOcr.LocateTesseract(data);
        if (tesseract is null || SubtitleOcr.FindModel(data, tesseract, "eng") is null) return;   // no OCR engine on this machine
        var sup = Path.Combine(fx.Dir, "subs.sup");
        File.WriteAllBytes(sup, PgsWriter.Write([await TextPicture("Hello there, General", 1, 3), await TextPicture("I have the high ground", 4, 6.5)]));
        var mkv = Path.Combine(fx.Dir, "pgs.mkv");
        var r = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=320x180:d=8",
            "-i", sup, "-copyts", "-map", "0:v", "-map", "1:s", "-c:v", "libx264", "-preset", "ultrafast", "-c:s", "copy", "-metadata:s:s:0", "language=eng", mkv]);
        Assert.True(r.ExitCode == 0, r.StdErr);
        var media = await new MediaProber(fx.Tools.Ffprobe).ProbeAsync(mkv);
        var track = Assert.Single(media.Subtitles);
        Assert.True(SubtitleOcr.CanRead(track.CodecName), track.CodecName);

        var srt = Path.Combine(fx.Dir, "pgs.srt");
        var res = await SubtitleOcr.RecognizeAsync(fx.Tools.Ffmpeg, tesseract, data, mkv, track.Index, track.Language, srt, fx.Dir);
        var text = File.ReadAllText(srt);
        Assert.Equal(2, res.Subtitles);
        Assert.Contains("00:00:01,000 --> 00:00:03,000", text);
        Assert.Contains("Hello there", text);
        Assert.Contains("high ground", text);
    }

    [Fact]
    public async Task Job_turns_pgs_into_text_subtitles_of_an_mp4()
    {
        if (fx.Tools is null) return;
        var store = new KnA.Core.Settings.SettingsStore(Path.Combine(fx.Dir, "data-ocr-job"));
        var tesseract = SubtitleOcr.LocateTesseract(store.DataDirectory);
        if (tesseract is null || SubtitleOcr.FindModel(store.DataDirectory, tesseract, "eng") is null) return;
        var sup = Path.Combine(fx.Dir, "job.sup");
        File.WriteAllBytes(sup, PgsWriter.Write([await TextPicture("Welcome to the jungle", 1, 3)]));
        var mkv = Path.Combine(fx.Dir, "pgs-job.mkv");
        var r = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x180:d=5:r=25",
            "-i", sup, "-copyts", "-map", "0:v", "-map", "1:s", "-c:v", "libx264", "-preset", "ultrafast", "-c:s", "copy", "-metadata:s:s:0", "language=eng", mkv]);
        Assert.True(r.ExitCode == 0, r.StdErr);
        using var q = new KnA.Core.Queue.QueueService(store, restoreQueue: false);
        var job = Assert.Single(q.AddPaths([mkv], new KnA.Core.Encoding.JobSettings
        {
            Profile = KnA.Core.Encoding.ProfileKind.Streaming, Codec = KnA.Core.Encoding.VideoCodec.H264, Resolution = 0,
            Quality = KnA.Core.Encoding.QualityLevel.Draft, Subtitles = KnA.Core.Encoding.SubtitleMode.Softsub, OcrPictureSubtitles = true,
        }));
        var done = new TaskCompletionSource();
        q.QueueChanged += () => { if (!q.IsRunning) done.TrySetResult(); };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (job.Media is null && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
        q.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(job.Status is KnA.Core.Queue.JobStatus.Done or KnA.Core.Queue.JobStatus.Warning, job.Message + "\n" + job.Log);
        Assert.EndsWith(".mp4", job.OutputPath);
        var outMedia = await new MediaProber(fx.Tools.Ffprobe).ProbeAsync(job.OutputPath!);
        var sub = Assert.Single(outMedia.Subtitles);
        Assert.Equal("mov_text", sub.CodecName);
        Assert.Equal("eng", sub.Language);
        var text = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-hide_banner", "-v", "error", "-i", job.OutputPath!, "-map", "0:s:0", "-f", "srt", "-"]);
        Assert.Contains("jungle", text.StdOut);
        Assert.Contains("OCR:", job.Log);
    }

    private static SubtitleImage ToImage(PgsWriter.Picture p)
    {
        var rgba = new byte[p.Width * p.Height * 4];
        for (int i = 0; i < p.Indexed.Length; i++)
        {
            byte v = p.Indexed[i] switch { 1 => 235, 3 => 128, 2 => 16, _ => 0 };
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = v;
            rgba[i * 4 + 3] = p.Indexed[i] == 0 ? (byte)0 : (byte)255;
        }
        return new SubtitleImage(TimeSpan.Zero, TimeSpan.FromSeconds(1), p.Width, p.Height, rgba);
    }

    [Fact]
    public async Task Italic_type_is_found_from_the_slant_of_the_letters()
    {
        if (fx.Tools is null) return;
        var oblique = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono-BoldOblique.ttf";
        if (!File.Exists(oblique)) return;
        var upright = ToImage(await TextPicture("Where are you going", 0, 1));
        var italic = ToImage(await TextPicture("Where are you going", 0, 1, oblique));
        Assert.False(Italic.IsItalic(upright), $"upright slant {Italic.Slant(upright)}");
        Assert.True(Italic.IsItalic(italic), $"italic slant {Italic.Slant(italic)}");
    }

    [Fact]
    public async Task Dvd_subtitles_are_read_like_pgs()
    {
        if (fx.Tools is null) return;
        var data = Path.Combine(fx.Dir, "data-vobsub");
        var tesseract = SubtitleOcr.LocateTesseract(data);
        if (tesseract is null || SubtitleOcr.FindModel(data, tesseract, "eng") is null) return;
        var sup = Path.Combine(fx.Dir, "dvd.sup");
        File.WriteAllBytes(sup, PgsWriter.Write([await TextPicture("Keep the change", 1, 3)]));
        var mkv = Path.Combine(fx.Dir, "dvd.mkv");
        // FFmpeg turns picture subtitles into DVD ones (dvdsub encoder): a real VobSub track with its palette
        var r = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=720x480:d=5",
            "-i", sup, "-copyts", "-map", "0:v", "-map", "1:s", "-c:v", "libx264", "-preset", "ultrafast", "-c:s", "dvdsub", "-metadata:s:s:0", "language=eng", mkv]);
        Assert.True(r.ExitCode == 0, r.StdErr);
        var media = await new MediaProber(fx.Tools.Ffprobe).ProbeAsync(mkv);
        var track = Assert.Single(media.Subtitles);
        Assert.Equal("dvd_subtitle", track.CodecName);
        var images = await SubtitleOcr.LoadImagesAsync(fx.Tools.Ffmpeg, fx.Tools.Ffprobe, mkv, track.Index, track.CodecName, fx.Dir, default);
        var img = Assert.Single(images);
        Assert.Equal(1.0, img.Start.TotalSeconds, 1);
        Assert.InRange(img.End.TotalSeconds, 2.8, 5.01);   // FFmpeg's PGS→DVD conversion carries no end: KnA falls back to 4 s
        var srt = Path.Combine(fx.Dir, "dvd.srt");
        var res = await SubtitleOcr.RecognizeAsync(fx.Tools.Ffmpeg, tesseract, data, mkv, track.Index, track.Language, srt, fx.Dir, codec: track.CodecName);
        Assert.Equal(1, res.Subtitles);
        Assert.Contains("change", File.ReadAllText(srt));
    }

    [Fact]
    public void Tesseract_tsv_gives_lines_and_confidence_per_picture()
    {
        const string tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n"
            + "5\t1\t1\t1\t1\t1\t0\t0\t1\t1\t96.5\tHello\n5\t1\t1\t1\t1\t2\t0\t0\t1\t1\t90.5\tthere\n"
            + "5\t1\t1\t1\t2\t1\t0\t0\t1\t1\t80\tfriend\n5\t2\t1\t1\t1\t1\t0\t0\t1\t1\t50\tBye\n";
        var pages = OcrEngine.ParseTsv(tsv).ToList();
        Assert.Equal((1, "Hello there\nfriend", 89.0), (pages[0].Page, pages[0].Text, Math.Round(pages[0].Confidence, 1)));
        Assert.Equal("Bye", pages[1].Text);
    }

    [Fact]
    public void Paddleocr_result_lines_are_parsed()
    {
        var b = PaddleOcr.ParseBox("[2025/01/01 10:00:00] ppocr INFO: [[[92.0, 56.0], [735.0, 60.0], [734.0, 118.0], [91.0, 113.0]], ('My mommy always said', 0.9907816052436829)]");
        Assert.NotNull(b);
        Assert.Equal("My mommy always said", b!.Value.Text);
        Assert.Equal(0.99, b.Value.Score, 2);
    }

    [Fact]
    public async Task Seven_zip_archives_open_with_the_python_engine()
    {
        var py = Environment.GetEnvironmentVariable("KNA_PYTHON");
        if (py is null || !File.Exists(py)) return;   // CI / developer machines set it; the portable Python is downloaded by the app
        var data = Path.Combine(fx.Dir, "data-archive");
        var cm = new ComponentManager(data);
        await ArchiveEngine.InstallAsync(cm, data, Path.Combine(fx.Dir, "tmp-archive"), null, null, default);
        Assert.True(ArchiveEngine.IsInstalled(data));
        // make a .7z with py7zr itself, then extract it through KnA
        var src = Path.Combine(fx.Dir, "arch-src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "seven zip ok");
        var archive = Path.Combine(fx.Dir, "test.7z");
        var lib = Path.Combine(ArchiveEngine.Folder(data), "lib");
        var r = await ProcessRunner.RunAsync(py, ["-c", $"import sys; sys.path.insert(0, r'{lib}'); import py7zr; z = py7zr.SevenZipFile(r'{archive}', 'w'); z.writeall(r'{src}', 'pkg'); z.close()"]);
        Assert.True(r.ExitCode == 0, r.StdErr);
        var dst = Path.Combine(fx.Dir, "arch-out");
        await ArchiveEngine.ExtractAsync(data, archive, dst, default);
        Assert.Equal("seven zip ok", File.ReadAllText(Path.Combine(dst, "pkg", "a.txt")));
    }

    [Fact]
    public async Task Ocr_session_reads_edits_and_replaces_the_picture_track_in_the_job()
    {
        if (fx.Tools is null) return;
        var store = new KnA.Core.Settings.SettingsStore(Path.Combine(fx.Dir, "data-ocr-session"));
        var tesseract = SubtitleOcr.LocateTesseract(store.DataDirectory);
        if (tesseract is null || SubtitleOcr.FindModel(store.DataDirectory, tesseract, "eng") is null) return;
        var dir = Path.Combine(fx.Dir, "session");
        Directory.CreateDirectory(dir);
        var sup = Path.Combine(dir, "s.sup");
        File.WriteAllBytes(sup, PgsWriter.Write([await TextPicture("First line here", 1, 2), await TextPicture("Second one", 3, 4)]));
        var mkv = Path.Combine(dir, "movie.mkv");
        var r = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "color=black:s=320x180:d=5",
            "-i", sup, "-copyts", "-map", "0:v", "-map", "1:s", "-c:v", "libx264", "-preset", "ultrafast", "-c:s", "copy", "-metadata:s:s:0", "language=eng", mkv]);
        Assert.True(r.ExitCode == 0, r.StdErr);
        using var q = new KnA.Core.Queue.QueueService(store, restoreQueue: false);
        var job = Assert.Single(q.AddPaths([mkv], new KnA.Core.Encoding.JobSettings { Subtitles = KnA.Core.Encoding.SubtitleMode.Softsub }));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (job.Media is null && sw.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(50);
        var pgs = job.Media!.Subtitles.Single();

        var s = await OcrSession.OpenAsync(q, job, mkv, pgs.Index);
        Assert.Equal(2, s.Lines.Count);
        await s.RecognizeAsync(q, new OcrOptions(), null, null, default);
        Assert.Contains("First", s.Lines[0].Text);
        Assert.True(s.Lines[0].Confidence > 50, s.Lines[0].Confidence.ToString());
        Assert.True(s.Png(0).Length > 100);

        s.Edit(1, text: "Second one, fixed by hand", italic: true);
        await s.RecognizeAsync(q, new OcrOptions(), null, null, default);   // hand edits survive a new pass
        Assert.Equal("Second one, fixed by hand", s.Lines[1].Text);
        Assert.Equal(1, s.Replace("fixed by hand", "corrected", false, false));

        var a = await s.UseInJobAsync(q, s.DefaultSrtPath());
        Assert.EndsWith("movie.eng.srt", s.DefaultSrtPath());
        Assert.Contains("<i>Second one, corrected</i>", File.ReadAllText(s.DefaultSrtPath()));
        var subs = a.Tracks.Where(t => t.Kind == "subtitle").ToList();
        Assert.False(subs.Single(t => t.File == mkv).Include);
        var text = subs.Single(t => t.File == s.DefaultSrtPath());
        Assert.True(text.Include);
        Assert.Equal("eng", text.Language);
    }
}
