using KnA.Core.Encoding;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.Core.Tests;

/// <summary>Smart drag and drop and watch folders.</summary>
public class WorkflowTests(FeatureFixture fx) : IClassFixture<FeatureFixture>
{
    private QueueService NewQueue(string name) => new(new SettingsStore(Path.Combine(fx.Dir, "data-wf-" + name)), restoreQueue: false);

    [Theory]
    [InlineData("Ep01.ass", "Ep01.mkv", true)]
    [InlineData("Ep01.ita.ass", "Ep01.mkv", true)]
    [InlineData("Ep01 [Commentary].flac", "Ep01.mkv", true)]
    [InlineData("Ep012.ass", "Ep01.mkv", false)]
    [InlineData("Other.ass", "Ep01.mkv", false)]
    public void External_names_match_their_video(string ext, string video, bool expected) =>
        Assert.Equal(expected, DropClassifier.BelongsTo(ext, video));

    [Fact]
    public async Task Drop_classifies_and_attaches()
    {
        if (fx.Tools is null) return;
        var dir = Directory.CreateDirectory(Path.Combine(fx.Dir, "drop")).FullName;
        var video = Path.Combine(dir, "Show Ep 05.mkv");
        File.Copy(fx.Black, video, true);
        var subs = Path.Combine(dir, "Show Ep 05.ita.ass");
        File.Copy(fx.ExtSubs, subs, true);
        var junk = Path.Combine(dir, "notes.txt");
        File.WriteAllText(junk, "x");
        var preset = Path.Combine(dir, "my preset.json");
        File.WriteAllText(preset, """{ "name": "Dropped", "settings": { "profile": "Streaming", "codec": "H264" } }""");

        using var q = NewQueue("drop");
        var plan = DropClassifier.Classify([video, subs, junk, preset], q);
        Assert.Equal(DropKind.Video, plan.Items.Single(i => i.Path == video).Kind);
        Assert.Equal(video, plan.Items.Single(i => i.Path == subs).TargetVideo);
        Assert.Equal(DropKind.Rejected, plan.Items.Single(i => i.Path == junk).Kind);
        Assert.Equal(DropKind.Preset, plan.Items.Single(i => i.Path == preset).Kind);

        var r = await DropClassifier.ApplyAsync(plan, q, new JobSettings { Profile = ProfileKind.Streaming, Subtitles = SubtitleMode.Hardsub });
        Assert.Equal((1, 1, 1), (r.Added, r.Attached, r.Presets));
        var job = Assert.Single(q.Jobs);
        Assert.Contains(job.Edit.Tracks!, t => t.File == subs && t.Burn);
        Assert.Contains(q.Presets.All, p => p.Name == "Dropped");

        // Same video again: duplicate, not queued twice.
        Assert.Equal(DropKind.Duplicate, DropClassifier.Classify([video], q).Items[0].Kind);
    }

    [Fact]
    public async Task Watch_folder_queues_new_stable_files_with_its_preset()
    {
        if (fx.Tools is null) return;
        var watch = Directory.CreateDirectory(Path.Combine(fx.Dir, "watch")).FullName;
        var outDir = Directory.CreateDirectory(Path.Combine(fx.Dir, "watch-out")).FullName;
        File.Copy(fx.Black, Path.Combine(watch, "old.mkv"), true);          // already there: ignored
        using var q = NewQueue("watch");
        q.Presets.Save(new Preset("Watch preset", null, new JobSettings { Profile = ProfileKind.Streaming, Codec = VideoCodec.H264, Quality = QualityLevel.Draft }));
        using var w = new WatchFolderService(q);
        w.Save([new WatchFolder { Path = watch, Preset = "Watch preset", OutputFolder = outDir, AutoStart = false, IntervalSeconds = 5, SettleSeconds = 0 }]);

        File.Copy(fx.Black, Path.Combine(watch, "new.mkv"), true);
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (q.Jobs.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(500);
        var job = Assert.Single(q.Jobs);
        Assert.EndsWith("new.mkv", job.SourcePath);
        Assert.Equal("Watch preset", job.Settings.PresetName);
        Assert.Equal(ProfileKind.Streaming, job.Settings.Profile);
        Assert.Equal(outDir, job.Edit.OutputFolder);

        // A restart remembers what was queued.
        using var q2 = NewQueue("watch");
        using var w2 = new WatchFolderService(q2);
        w2.ScanNow(q2.Settings.WatchFolders[0].Id);
        await Task.Delay(6000);
        w2.ScanNow(q2.Settings.WatchFolders[0].Id);
        await Task.Delay(500);
        Assert.Empty(q2.Jobs);
    }
}
