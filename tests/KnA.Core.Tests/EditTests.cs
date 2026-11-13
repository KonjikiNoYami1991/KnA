using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Processing;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.Core.Tests;

/// <summary>Track list (all tracks, per-track output), chapters, attachments and multi-segment cuts.</summary>
public class EditTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    private QueueService NewQueue(string name) => new(new SettingsStore(Path.Combine(fx.Dir, "data-edit-" + name)), restoreQueue: false);

    private static readonly JobSettings Fast = new()
    {
        Profile = ProfileKind.UniversalMkv, Codec = VideoCodec.H264, Quality = QualityLevel.Draft, Resolution = 0, Subtitles = SubtitleMode.Softsub,
    };

    private static async Task Finish(QueueService q, params QueueJob[] jobs)
    {
        var done = new TaskCompletionSource();
        q.QueueChanged += () => { if (!q.IsRunning) done.TrySetResult(); };
        q.Start();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(4));
        foreach (var job in jobs) Assert.True(job.Status is JobStatus.Done or JobStatus.Warning, job.Message + "\n" + job.Log);
    }

    private async Task<MediaInfo> Probe(string path) => await new MediaProber(fx.Tools!.Ffprobe).ProbeAsync(path, requireVideo: false);

    // ------------------------------------------------------------------ pure

    [Fact]
    public void Kept_parts_are_sorted_clamped_and_stored_canonically()
    {
        var e = new JobEdit().WithParts([new TrimSegment(30, 40), new TrimSegment(5, 10)], 60);
        Assert.True(e.IsMultiSegment);
        Assert.Equal([(5.0, 10.0), (30.0, 40.0)], e.KeptParts(60));
        var single = new JobEdit().WithParts([new TrimSegment(0, 60)], 60);
        Assert.False(single.IsTrimmed);
        var one = new JobEdit().WithParts([new TrimSegment(2, null)], 60);
        Assert.Equal(2, one.TrimStart);
        Assert.Null(one.Segments);
        Assert.Null(e.Untrimmed.Segments);
    }

    [Fact]
    public void Chapters_are_remapped_onto_the_joined_parts()
    {
        var marks = new List<ChapterMark> { new(0, "Intro"), new(12, "Middle"), new(15, "Gone"), new(31, "End") };
        var r = Chapters.Remap(marks, [(0, 10), (20, 40)]);
        // "Middle" and "Gone" are in the removed part → both at the start of the 2nd part (10 s): the last one wins.
        Assert.Equal(["Intro", "Gone", "End"], r.Select(c => c.Title));
        Assert.Equal(10, r[1].Start, 3);
        Assert.Equal(21, r[2].Start, 3);
        var meta = Chapters.ToFfmetadata([new ChapterMark(0, "A=B"), new ChapterMark(5, null)], 10);
        Assert.Contains("title=A\\=B", meta);
        Assert.Contains("START=5000", meta);
        Assert.Contains("END=10000", meta);
    }

    [Fact]
    public void Subtitle_choice_respects_the_container()
    {
        var text = new SubtitleStream { Index = 2, Kind = "subtitle", CodecName = "subrip" };
        var pgs = new SubtitleStream { Index = 3, Kind = "subtitle", CodecName = "hdmv_pgs_subtitle" };
        Assert.Equal("ass", Containers.ChosenSubtitleCodec("mkv", text, null, "ass"));
        Assert.Null(Containers.ChosenSubtitleCodec("mp4", text, null, "srt"));
        Assert.Equal("mov_text", Containers.ChosenSubtitleCodec("mp4", text, null, "mov_text"));
        Assert.Null(Containers.ChosenSubtitleCodec("mkv", pgs, null, "srt"));
        Assert.Equal("copy", Containers.ChosenSubtitleCodec("mkv", pgs, null, "copy"));
    }

    // ------------------------------------------------------------------ with FFmpeg

    [Fact]
    public async Task Every_track_is_listed_even_in_demux_and_old_custom_plans()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("list");
        var job = Assert.Single(q.AddPaths([fx.Black], new JobSettings { Profile = ProfileKind.Demux }));
        var a = await q.AnalyzeAsync(job);
        Assert.Contains(a.Tracks, t => t.Kind == "audio" && t.Include);
        // A saved plan without the audio track (older versions) gets it back, switched off.
        await q.SetEditAsync(job.Id, new JobEdit { Tracks = [.. a.Tracks.Where(t => t.Kind == "video")] });
        a = await q.AnalyzeAsync(job);
        Assert.Contains(a.Tracks, t => t.Kind == "audio" && !t.Include);
        Assert.DoesNotContain("0:1", a.CommandLine!);   // unticked → not extracted
    }

    [Fact]
    public async Task Multi_segment_job_joins_parts_and_moves_chapters()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("join");
        var job = Assert.Single(q.AddPaths([fx.Black], Fast));
        await q.AnalyzeAsync(job);
        var edit = new JobEdit { Chapters = [new(0, "One"), new(14, "Two")] }.WithParts([new TrimSegment(2, 5), new TrimSegment(12, 16)], 20);
        var a = await q.SetEditAsync(job.Id, edit);
        Assert.DoesNotContain(a.Issues, i => i.IsError);
        Assert.Equal(7, a.OutputDuration.TotalSeconds, 1);
        await Finish(q, job);
        var m = await Probe(job.OutputPath!);
        Assert.InRange(m.Duration.TotalSeconds, 6.7, 7.4);
        Assert.Single(m.Audio);
        Assert.Equal(["One", "Two"], m.Chapters.Select(c => c.Title));
        Assert.InRange(m.Chapters[1].Start, 4.9, 5.1);   // 14 s → 3 s of part 1 + 2 s into part 2
    }

    [Fact]
    public async Task Joined_segments_keep_subtitles_and_attachments()
    {
        if (fx.Tools is null || !File.Exists(fx.Font)) return;
        using var q = NewQueue("join-mkv");
        var job = Assert.Single(q.AddPaths([fx.Black], Fast));
        var a = await q.AnalyzeAsync(job);
        await q.SetEditAsync(job.Id, new JobEdit { ExternalFiles = [fx.ExtSubs] });
        a = await q.AnalyzeAsync(job);
        var tracks = a.Tracks.Select(t => t.File == fx.ExtSubs ? t with { Include = true } : t).Append(TrackPlanner.Attachment(fx.Font)).ToList();
        await q.SetEditAsync(job.Id, new JobEdit { ExternalFiles = [fx.ExtSubs], Tracks = tracks }.WithParts([new TrimSegment(1, 4), new TrimSegment(9, 13)], 20));
        await Finish(q, job);
        var m = await Probe(job.OutputPath!);
        Assert.InRange(m.Duration.TotalSeconds, 6.7, 7.4);
        Assert.Single(m.Subtitles);
        Assert.Single(m.Attachments);
        Assert.Empty(m.Chapters);
    }

    [Fact]
    public async Task Segments_split_into_separate_jobs_with_part_names()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("split");
        var job = Assert.Single(q.AddPaths([fx.Black], Fast));
        await q.AnalyzeAsync(job);
        await q.SetEditAsync(job.Id, new JobEdit().WithParts([new TrimSegment(1, 3), new TrimSegment(10, 12), new TrimSegment(15, 16)], 20));
        var jobs = q.SplitSegments(job.Id);
        Assert.Equal(3, jobs.Count);
        Assert.Same(job, jobs[0]);
        Assert.All(jobs, j => Assert.False(j.Edit.IsMultiSegment));
        Assert.Equal(10, jobs[1].Edit.TrimStart);
        await Finish(q, [.. jobs]);
        Assert.Contains("part 2", Path.GetFileName(jobs[1].OutputPath!));
        Assert.InRange((await Probe(jobs[2].OutputPath!)).Duration.TotalSeconds, 0.8, 1.2);
    }

    [Fact]
    public async Task Per_track_output_attachments_and_custom_chapters()
    {
        if (fx.Tools is null || !File.Exists(fx.Font)) return;
        using var q = NewQueue("tracks");
        var job = Assert.Single(q.AddPaths([fx.Black], Fast));
        var a = await q.AnalyzeAsync(job);
        var cover = Path.Combine(fx.Dir, "notes.txt");
        await File.WriteAllTextAsync(cover, "hello");
        var tracks = a.Tracks.Select(t => t.Kind == "audio" ? t with { Output = "Opus", Bitrate = 96 } : t)
            .Append(TrackPlanner.Attachment(cover)).Append(TrackPlanner.Attachment(fx.Font)).ToList();
        await q.SetEditAsync(job.Id, new JobEdit { Tracks = tracks, Chapters = [new(0, "Start"), new(10, "Half")], TrimEnd = 15 });
        await Finish(q, job);
        var m = await Probe(job.OutputPath!);
        Assert.Equal("opus", Assert.Single(m.Audio).CodecName);
        Assert.Equal(2, m.Attachments.Count);
        Assert.Contains(m.Attachments, x => x.FileName == "notes.txt" && x.MimeType == "text/plain");
        Assert.Equal(["Start", "Half"], m.Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task Video_can_be_copied_from_the_track_list()
    {
        if (fx.Tools is null) return;
        using var q = NewQueue("vcopy");
        var job = Assert.Single(q.AddPaths([fx.Black], Fast with { Codec = VideoCodec.HEVC }));
        var a = await q.AnalyzeAsync(job);
        var tracks = a.Tracks.Select(t => t.Kind == "video" ? t with { Output = "copy" } : t).ToList();
        a = await q.SetEditAsync(job.Id, new JobEdit { Tracks = tracks });
        Assert.Contains("-c:v copy", a.CommandLine!);
        await Finish(q, job);
        Assert.Equal("h264", (await Probe(job.OutputPath!)).PrimaryVideo!.CodecName);
    }

    [Fact]
    public async Task Demux_extracts_only_the_chosen_attachment_and_edited_chapters()
    {
        if (fx.Tools is null || !File.Exists(fx.Font)) return;
        var withFonts = Path.Combine(fx.Dir, "fonts.mkv");
        var other = Path.Combine(fx.Dir, "Other.ttf");
        File.Copy(fx.Font, other, true);
        var r = await ProcessRunner.RunAsync(fx.Tools.Ffmpeg, ["-v", "error", "-y", "-i", fx.Black, "-t", "3", "-map", "0", "-c", "copy",
            "-attach", fx.Font, "-metadata:s:2", "mimetype=application/x-truetype-font", "-attach", other, "-metadata:s:3", "mimetype=application/x-truetype-font", withFonts]);
        Assert.Equal(0, r.ExitCode);
        using var q = NewQueue("demux");
        var job = Assert.Single(q.AddPaths([withFonts], new JobSettings { Profile = ProfileKind.Demux }));
        var a = await q.AnalyzeAsync(job);
        var tracks = a.Tracks.Select(t => t.Kind == "attachment" && t.Stream == 3 ? t with { Include = false } : t).ToList();
        await q.SetEditAsync(job.Id, new JobEdit { Tracks = tracks, Chapters = [new(0, "Only")] });
        await Finish(q, job);
        var files = Directory.GetFiles(job.OutputPath!).Select(Path.GetFileName).ToList();
        Assert.Contains("MyFont.ttf", files);
        Assert.DoesNotContain("Other.ttf", files);
        var chapters = Assert.Single(Directory.GetFiles(job.OutputPath!, "*.chapters.txt"));
        Assert.Contains("title=Only", await File.ReadAllTextAsync(chapters));
    }
}

public class AiRuntimeTests
{
    [Fact]
    public void Portable_python_picks_the_newest_patch_of_the_preferred_minor()
    {
        string[] names =
        [
            "cpython-3.11.14+20251031-x86_64-pc-windows-msvc-install_only.tar.gz",
            "cpython-3.12.11+20251031-x86_64-pc-windows-msvc-install_only.tar.gz",
            "cpython-3.12.12+20251031-x86_64-pc-windows-msvc-install_only.tar.gz",
            "cpython-3.12.12+20251031-x86_64-pc-windows-msvc-install_only_stripped.tar.gz",
            "cpython-3.13.9+20251031-x86_64-pc-windows-msvc-install_only.tar.gz",
            "cpython-3.12.12+20251031-x86_64-unknown-linux-gnu-install_only.tar.gz",
        ];
        Assert.Equal("cpython-3.12.12+20251031-x86_64-pc-windows-msvc-install_only.tar.gz",
            KnA.Core.Tools.ComponentManager.PickPortablePython(names, "x86_64-pc-windows-msvc"));
        Assert.Null(KnA.Core.Tools.ComponentManager.PickPortablePython(names, "aarch64-apple-darwin"));
    }

    [Fact]
    public void Torch_index_gives_the_builds_of_the_newest_version()
    {
        const string html = """
            <a href="/whl/cu126/torch-2.9.0%2Bcu126-cp312-cp312-win_amd64.whl#sha256=1">torch-2.9.0+cu126-cp312-cp312-win_amd64.whl</a>
            <a href="/whl/cu130/torch-2.10.0%2Bcu130-cp312-cp312-win_amd64.whl#sha256=2">torch-2.10.0+cu130-cp312-cp312-win_amd64.whl</a>
            <a href="/whl/cu128/torch-2.10.0%2Bcu128-cp313-cp313t-win_amd64.whl#sha256=3">x</a>
            <a href="/whl/cu132/torch-2.10.0%2Bcu132-cp312-cp312-manylinux_2_28_x86_64.whl#sha256=4">x</a>
            <a href="/whl/rocm7.1/torch-2.10.0%2Brocm7.1-cp312-cp312-manylinux_2_28_x86_64.whl#sha256=5">x</a>
            """;
        var win = KnA.Core.Tools.ComponentManager.ParseTorchTags(html, "cu", "win_amd64");
        Assert.Equal(["cu130", "cu128"], win);
        Assert.Equal(["cu132"], KnA.Core.Tools.ComponentManager.ParseTorchTags(html, "cu", "x86_64"));
        Assert.Equal(["rocm7.1"], KnA.Core.Tools.ComponentManager.ParseTorchTags(html, "rocm", "x86_64"));
        Assert.Equal(12.8, KnA.Core.Tools.ComponentManager.CudaOf("cu128"), 3);
        Assert.Equal(13.0, KnA.Core.Tools.ComponentManager.CudaOf("cu130"), 3);
    }
}
