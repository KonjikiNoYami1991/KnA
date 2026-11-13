using System.Buffers.Binary;
using System.Text;
using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Media.Discs;
using KnA.Core.Processing;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.Core.Tests;

/// <summary>
/// Synthetic Blu-ray (BDMV with MPLS playlists and .m2ts clips) and DVD (VIDEO_TS with IFO and split VOBs)
/// structures, plus a DVD ISO image, built byte by byte from the specifications.
/// </summary>
public sealed class DiscFixture : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("kna-disc-").FullName;
    public FfmpegTools? Tools { get; }
    public string Bd => Path.Combine(Dir, "My Show BD1");
    public string Dvd => Path.Combine(Dir, "Old Movie DVD");
    public string DvdIso => Path.Combine(Dir, "Old Movie.iso");

    public DiscFixture()
    {
        Tools = FfmpegLocator.Locate(null, Dir);
        if (Tools is null) return;

        // ---------------- Blu-ray: two 2 s clips; playlist 1 = both (episode), 2 = duplicate of 1, 3 = second clip only
        var stream = Directory.CreateDirectory(Path.Combine(Bd, "BDMV", "STREAM")).FullName;
        var playlist = Directory.CreateDirectory(Path.Combine(Bd, "BDMV", "PLAYLIST")).FullName;
        File.WriteAllBytes(Path.Combine(Bd, "BDMV", "index.bdmv"), "INDX0200"u8.ToArray());
        foreach (var (clip, color) in new[] { ("00001", "red"), ("00002", "blue") })
            Run("-f", "lavfi", "-i", $"color={color}:size=320x240:rate=24:duration=2", "-f", "lavfi", "-i", "sine=duration=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "ac3", "-streamid", "0:0x1011", "-streamid", "1:0x1100",
                "-f", "mpegts", "-mpegts_m2ts_mode", "1", Path.Combine(stream, clip + ".m2ts"));
        File.WriteAllBytes(Path.Combine(playlist, "00001.mpls"), Mpls(["00001", "00002"], "jpn"));
        File.WriteAllBytes(Path.Combine(playlist, "00002.mpls"), Mpls(["00001", "00002"], "jpn"));
        File.WriteAllBytes(Path.Combine(playlist, "00003.mpls"), Mpls(["00002"], "eng"));

        // ---------------- DVD: title 1 (VTS 1, 0:00:03) in two VOB parts, title 2 (VTS 2, 0:00:01)
        var vt = Directory.CreateDirectory(Path.Combine(Dvd, "VIDEO_TS")).FullName;
        var vob = Path.Combine(Dir, "title.vob");
        Run("-f", "lavfi", "-i", "testsrc2=size=720x576:rate=25:duration=3", "-f", "lavfi", "-i", "sine=duration=3",
            "-c:v", "mpeg2video", "-c:a", "mp2", "-f", "vob", vob);
        var bytes = File.ReadAllBytes(vob);
        int half = bytes.Length / 2 / 2048 * 2048;
        File.WriteAllBytes(Path.Combine(vt, "VTS_01_1.VOB"), bytes[..half]);
        File.WriteAllBytes(Path.Combine(vt, "VTS_01_2.VOB"), bytes[half..]);
        File.WriteAllBytes(Path.Combine(vt, "VTS_02_1.VOB"), bytes[..half]);
        File.WriteAllBytes(Path.Combine(vt, "VIDEO_TS.IFO"), Vmg([(1, 1, 5), (2, 1, 1)]));
        File.WriteAllBytes(Path.Combine(vt, "VTS_01_0.IFO"), Vts(1, new TimeSpan(0, 0, 3)));
        File.WriteAllBytes(Path.Combine(vt, "VTS_02_0.IFO"), Vts(1, new TimeSpan(0, 0, 1)));
        WriteIso(DvdIso, vt);
    }

    // ------------------------------------------------------------ MPLS

    public static byte[] Mpls(string[] clips, string audioLang)
    {
        var items = new List<byte>();
        foreach (var c in clips)
        {
            var stn = new List<byte> { 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }; // reserved(2) + 7 counts + 5 reserved
            stn.AddRange([9, 1, 0x10, 0x11, 0, 0, 0, 0, 0, 0, 5, 0x1B, 0x61, 0, 0, 0]);          // video PID 0x1011, H.264
            stn.AddRange([9, 1, 0x11, 0x00, 0, 0, 0, 0, 0, 0, 5, 0x81, 0x31, .. System.Text.Encoding.ASCII.GetBytes(audioLang)]); // AC-3, language
            var body = new List<byte>();
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(c + "M2TS"));
            body.AddRange([0, 1, 0]);                       // flags, STC id
            body.AddRange(Be32(0)); body.AddRange(Be32(2 * 45000));
            body.AddRange(new byte[12]);                    // UO mask (8), random access, still mode, still time
            body.AddRange(Be16(stn.Count)); body.AddRange(stn);
            items.AddRange(Be16(body.Count)); items.AddRange(body);
        }
        const int plStart = 64;
        var pl = new List<byte>();
        pl.AddRange(Be32(6 + items.Count)); pl.AddRange([0, 0]); pl.AddRange(Be16(clips.Length)); pl.AddRange(Be16(0)); pl.AddRange(items);
        int markStart = plStart + pl.Count;
        var marks = new List<byte>();
        marks.AddRange(Be32(2 + 14 * clips.Length)); marks.AddRange(Be16(clips.Length));
        for (int i = 0; i < clips.Length; i++) { marks.AddRange([0, 1]); marks.AddRange(Be16(i)); marks.AddRange(Be32(0)); marks.AddRange([0xFF, 0xFF]); marks.AddRange(Be32(0)); }
        var head = new byte[plStart];
        System.Text.Encoding.ASCII.GetBytes("MPLS0200").CopyTo(head, 0);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), plStart);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), (uint)markStart);
        return [.. head, .. pl, .. marks];
    }

    // ------------------------------------------------------------ IFO

    private static byte[] Vmg((int Vts, int Ttn, int Chapters)[] titles)
    {
        var d = new byte[2048 + 8 + 12 * titles.Length];
        System.Text.Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0xC4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2048), (ushort)titles.Length);
        for (int i = 0; i < titles.Length; i++)
        {
            int e = 2048 + 8 + i * 12;
            d[e + 1] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(e + 2), (ushort)titles[i].Chapters);
            d[e + 6] = (byte)titles[i].Vts;
            d[e + 7] = (byte)titles[i].Ttn;
        }
        return d;
    }

    private static byte[] Vts(int ttn, TimeSpan t)
    {
        var d = new byte[2048 + 16 + 16];
        System.Text.Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(0xCC), 1);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(2048), 1);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(2048 + 8), 0x80000000u | (uint)ttn << 24);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(2048 + 12), 16);
        static byte Bcd(int v) => (byte)((v / 10) << 4 | v % 10);
        int pgc = 2048 + 16;
        d[pgc + 4] = Bcd(t.Hours); d[pgc + 5] = Bcd(t.Minutes); d[pgc + 6] = Bcd(t.Seconds); d[pgc + 7] = 0x40;
        return d;
    }

    // ------------------------------------------------------------ ISO 9660 (just a root with VIDEO_TS)

    private static void WriteIso(string path, string videoTs)
    {
        var files = Directory.GetFiles(videoTs).Order().Select(f => (Name: Path.GetFileName(f), Data: File.ReadAllBytes(f))).ToList();
        const int S = 2048;
        int rootLba = 18, vtLba = 19, next = 20;
        var extents = new List<(string Name, int Lba, byte[] Data)>();
        foreach (var f in files) { extents.Add((f.Name, next, f.Data)); next += (f.Data.Length + S - 1) / S; }
        var img = new byte[next * S];
        // Primary volume descriptor + terminator
        img[16 * S] = 1; System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(img, 16 * S + 1); img[16 * S + 6] = 1;
        Record(img, 16 * S + 156, rootLba, S, true, [0]);
        img[17 * S] = 255; System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(img, 17 * S + 1);
        int p = rootLba * S;
        p += Record(img, p, rootLba, S, true, [0]);
        p += Record(img, p, rootLba, S, true, [1]);
        Record(img, p, vtLba, S, true, System.Text.Encoding.ASCII.GetBytes("VIDEO_TS"));
        p = vtLba * S;
        p += Record(img, p, vtLba, S, true, [0]);
        p += Record(img, p, rootLba, S, true, [1]);
        foreach (var e in extents)
        {
            p += Record(img, p, e.Lba, e.Data.Length, false, System.Text.Encoding.ASCII.GetBytes(e.Name + ";1"));
            e.Data.CopyTo(img, e.Lba * S);
        }
        File.WriteAllBytes(path, img);
    }

    private static int Record(byte[] img, int at, int lba, int size, bool dir, byte[] name)
    {
        int len = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        img[at] = (byte)len;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(at + 2), (uint)lba);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(at + 6), (uint)lba);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(at + 10), (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(img.AsSpan(at + 14), (uint)size);
        img[at + 25] = (byte)(dir ? 2 : 0);
        img[at + 32] = (byte)name.Length;
        name.CopyTo(img, at + 33);
        return len;
    }

    private static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];
    private static byte[] Be32(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

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

[CollectionDefinition("discs", DisableParallelization = true)]
public class DiscCollection;

[Collection("discs")]
public class DiscTests(DiscFixture fx) : IClassFixture<DiscFixture>
{
    private QueueService NewQueue(string name)
    {
        var q = new QueueService(new SettingsStore(Path.Combine(fx.Dir, "data-" + name)), restoreQueue: false);
        // Exercise KnA's own readers (no libbluray / dvdvideo), whatever this FFmpeg has.
        DiscSource.Support = DiscSupport.None;
        DiscSource.MinTitleLength = TimeSpan.FromSeconds(2);
        return q;
    }

    [Fact]
    public void Mpls_round_trip()
    {
        var p = MplsParser.Parse(1, DiscFixture.Mpls(["00001", "00002"], "jpn"));
        Assert.Equal(2, p.Items.Count);
        Assert.Equal(4, p.Duration.TotalSeconds, 2);
        Assert.Equal([0.0, 2.0], p.Chapters);
        Assert.Equal("jpn", p.Languages[0x1100]);
    }

    [Fact]
    public void Detects_disc_folders_and_images()
    {
        if (fx.Tools is null) return;
        Assert.Equal(DiscKind.BluRay, DiscSource.Detect(fx.Bd)!.Kind);
        Assert.Equal(fx.Bd, DiscSource.Detect(Path.Combine(fx.Bd, "BDMV"))!.Root);
        Assert.Equal(DiscKind.Dvd, DiscSource.Detect(Path.Combine(fx.Dvd, "VIDEO_TS"))!.Kind);
        var iso = DiscSource.Detect(fx.DvdIso)!;
        Assert.True(iso.IsIso);
        Assert.Equal(DiscKind.Dvd, iso.Kind);
        Assert.True(DiscSource.IsInsideDiscStructure(Path.Combine(fx.Bd, "BDMV", "STREAM", "00001.m2ts")));
    }

    [Fact]
    public async Task Titles_from_playlists_and_ifo()
    {
        if (fx.Tools is null) return;
        DiscSource.MinTitleLength = TimeSpan.FromSeconds(2);
        var bd = await DiscSource.ListTitlesAsync(DiscSource.Detect(fx.Bd)!, null);
        Assert.Equal([1, 3], bd.Select(t => t.Number));          // 00002 duplicates 00001
        var dvd = await DiscSource.ListTitlesAsync(DiscSource.Detect(fx.Dvd)!, null);
        Assert.Equal(2, dvd.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), dvd[0].Duration);
        Assert.Equal(5, dvd[0].Chapters);
        var iso = await DiscSource.ListTitlesAsync(DiscSource.Detect(fx.DvdIso)!, null);
        Assert.Equal(dvd.Select(t => t.Duration), iso.Select(t => t.Duration));
    }

    [Fact]
    public async Task Bluray_folder_title_is_encoded_with_languages()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("bd");
        var jobs = q.AddPaths([fx.Bd], new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 0, Subtitles = SubtitleMode.None });
        Assert.Equal(2, jobs.Count);                               // playlists 1 and 3, both ≥ 2 s
        var job = jobs[0];
        Assert.StartsWith("[BD] My Show BD1", job.FileName);
        var a = await q.AnalyzeAsync(job);
        Assert.Equal("jpn", a.Source.Audio[0].Language);
        q.Remove([jobs[1].Id]);
        await Run(q, job);
        Assert.StartsWith(Path.Combine(fx.Dir, "My Show BD1 - playlist 01"), job.OutputPath);
        var m = await new MediaProber(fx.Tools.Ffprobe).ProbeAsync(job.OutputPath!);
        Assert.InRange(m.Duration.TotalSeconds, 3.5, 4.6);
        Assert.Equal("jpn", m.Audio[0].Language);
    }

    [Fact]
    public async Task Dvd_folder_title_is_encoded()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("dvd");
        var jobs = q.AddPaths([fx.Dvd], new JobSettings { Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 0, Subtitles = SubtitleMode.None });
        var job = Assert.Single(jobs);                             // title 2 (1 s) is under the minimum
        await Run(q, job);
        var m = await new MediaProber(fx.Tools.Ffprobe).ProbeAsync(job.OutputPath!);
        Assert.InRange(m.Duration.TotalSeconds, 2.5, 3.5);
        Assert.Equal(720, m.PrimaryVideo!.Width);
    }

    [Fact]
    public void Dvd_image_without_dvdvideo_explains_what_is_needed()
    {
        if (fx.Tools is null) return;
        DiscSource.Support = DiscSupport.None;
        var path = DiscSource.TitlePath(DiscSource.Detect(fx.DvdIso)!, 1);
        var ex = Assert.Throws<InvalidOperationException>(() => DiscSource.InputArgs(path));
        Assert.Contains("FFmpeg 7", ex.Message);
    }

    [Fact]
    public void Native_readers_are_used_when_ffmpeg_supports_them()
    {
        if (fx.Tools is null) return;
        try
        {
            DiscSource.Support = new DiscSupport(true, true);
            var bd = DiscSource.InputArgs(DiscSource.TitlePath(DiscSource.Detect(fx.Bd)!, 3));
            Assert.Equal(["-playlist", "3", "-i", "bluray:" + fx.Bd], bd[^4..]);
            var dvd = DiscSource.InputArgs(DiscSource.TitlePath(DiscSource.Detect(fx.DvdIso)!, 2));
            Assert.Equal(["-f", "dvdvideo", "-title", "2", "-i", fx.DvdIso], dvd[^6..]);
        }
        finally { DiscSource.Support = DiscSupport.None; }
    }

    private static async Task Run(QueueService q, QueueJob job)
    {
        var done = new TaskCompletionSource();
        q.QueueChanged += () => { if (!q.IsRunning) done.TrySetResult(); };
        q.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(job.Status is JobStatus.Done or JobStatus.Warning, job.Message + "\n" + job.Log);
    }
}
