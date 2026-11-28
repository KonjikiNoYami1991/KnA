using KnA.Core.Accounts;
using KnA.Core.Encoding;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.Core.Tests;

/// <summary>Web accounts: each one's presets, settings and jobs (the server's per-user queue).</summary>
public sealed class UserDataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kna-users-").FullName;
    private static readonly Guid Alice = Guid.NewGuid(), Bob = Guid.NewGuid();

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static Preset P(string name, string? extra = null) =>
        new(name, null, new JobSettings { Encoder = new EncoderOverrides { Extra = extra } });

    // ------------------------------------------------------------------ presets

    [Fact]
    public void Each_account_sees_built_in_shared_and_its_own_presets()
    {
        var shared = new PresetStore(_dir);
        shared.Save(P("Shared A"));
        shared.Save(P("Common"));
        var data = new UserDataStore(_dir, shared);
        data.PresetsOf(Alice).Save(P("Alice only"));
        data.PresetsOf(Alice).Save(P("Common", "-tune animation"));   // her copy takes the shared one's place

        var alice = data.Visible(Alice, ownPresets: true);
        Assert.Equal(PresetStore.BuiltIns.Count + 3, alice.Count);
        Assert.Equal("shared", alice.Single(p => p.Name == "Shared A").Scope);
        Assert.Equal("own", alice.Single(p => p.Name == "Alice only").Scope);
        var common = Assert.Single(alice, p => p.Name == "Common");
        Assert.Equal("own", common.Scope);
        Assert.Equal("-tune animation", common.Settings.Encoder.Extra);
        Assert.All(alice.Where(p => p.BuiltIn), p => Assert.Null(p.Scope));

        // Bob sees nothing of Alice's.
        var bob = data.Visible(Bob, ownPresets: true);
        Assert.DoesNotContain(bob, p => p.Name == "Alice only");
        Assert.Null(bob.Single(p => p.Name == "Common").Settings.Encoder.Extra);
        Assert.Equal("-tune animation", data.Get(Alice, true, "common")!.Settings.Encoder.Extra);
        Assert.Null(data.Get(Bob, true, "Common")!.Settings.Encoder.Extra);
        Assert.Null(data.Get(Bob, true, "Alice only"));

        // Administrators (no own presets): the shared ones, not the users'.
        var admin = data.Visible(Guid.NewGuid(), ownPresets: false);
        Assert.DoesNotContain(admin, p => p.Name == "Alice only");
        Assert.Contains(admin, p => p.Name == "Shared A");

        var all = data.AllOwnPresets();
        Assert.Equal(2, all[Alice].Count);
        Assert.False(all.ContainsKey(Bob));
    }

    [Fact]
    public void The_scope_of_a_view_is_never_stored()
    {
        var shared = new PresetStore(_dir);
        var saved = shared.Save(P("X") with { Scope = "own" });
        Assert.Null(saved.Scope);
        Assert.DoesNotContain("scope", File.ReadAllText(Path.Combine(_dir, "presets.json")));
    }

    [Fact]
    public void Own_settings_round_trip_and_go_with_the_account()
    {
        var data = new UserDataStore(_dir, new PresetStore(_dir));
        Assert.Null(data.SettingsOf(Alice).Defaults);
        data.SaveSettings(Alice, new UserSettings { Defaults = new JobSettings { Resolution = 720 }, Theme = "Dark" });
        data.PresetsOf(Alice).Save(P("Mine"));
        var back = new UserDataStore(_dir, new PresetStore(_dir));
        Assert.Equal(720, back.SettingsOf(Alice).Defaults!.Resolution);
        Assert.Equal("Dark", back.SettingsOf(Alice).Theme);
        Assert.Null(back.SettingsOf(Bob).Theme);

        back.Delete(Alice);
        Assert.False(Directory.Exists(back.FolderOf(Alice)));
        Assert.Empty(back.PresetsOf(Alice).User);
        Assert.Null(back.SettingsOf(Alice).Defaults);
    }

    // ------------------------------------------------------------------ jobs

    private string Video(string name)
    {
        var path = Path.Combine(_dir, "media", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private QueueService NewQueue(bool restore = false) => new(new SettingsStore(Path.Combine(_dir, "data")), restoreQueue: restore);

    [Fact]
    public void Jobs_remember_their_account_across_restarts_and_splits()
    {
        using (var q = NewQueue())
        {
            q.AddPaths([Video("a.mkv")], new JobSettings(), null, null, Alice);
            q.AddPaths([Video("b.mkv")], new JobSettings());
            Assert.Equal(Alice, q.Jobs[0].Owner);
            Assert.Null(q.Jobs[1].Owner);
            q.SaveQueue();
        }
        using var again = NewQueue(restore: true);
        Assert.Equal(2, again.Jobs.Count);
        Assert.Equal(Alice, again.Jobs.Single(j => j.FileName == "a.mkv").Owner);
        Assert.Null(again.Jobs.Single(j => j.FileName == "b.mkv").Owner);
    }

    [Fact]
    public void A_user_moves_a_job_past_their_own_jobs_only()
    {
        using var q = NewQueue();
        q.AddPaths([Video("a1.mkv")], new JobSettings(), null, null, Alice);
        q.AddPaths([Video("b1.mkv"), Video("b2.mkv")], new JobSettings(), null, null, Bob);
        q.AddPaths([Video("a2.mkv")], new JobSettings(), null, null, Alice);
        string Order() => string.Join(",", q.Jobs.Select(j => Path.GetFileNameWithoutExtension(j.FileName)));

        var a2 = q.Jobs[3].Id;
        q.Move(a2, -1, j => j.Owner == Alice);             // one place up among Alice's: before a1
        Assert.Equal("a2,a1,b1,b2", Order());
        q.Move(a2, 1, j => j.Owner == Alice);              // and back down, past a1 only
        Assert.Equal("a1,a2,b1,b2", Order());
        q.Move(a2, 5, j => j.Owner == Alice);              // already her last: nothing moves
        Assert.Equal("a1,a2,b1,b2", Order());
        q.Move(a2, 1);                                     // administrators: every place counts
        Assert.Equal("a1,b1,a2,b2", Order());
    }

    [Fact]
    public void Stopping_a_users_jobs_leaves_the_others_waiting()
    {
        using var q = NewQueue();
        q.AddPaths([Video("a.mkv")], new JobSettings(), null, null, Alice);
        q.AddPaths([Video("b.mkv")], new JobSettings(), null, null, Bob);
        q.StopJobs(j => j.Owner == Alice);
        Assert.Equal(JobStatus.Stopped, q.Jobs.Single(j => j.Owner == Alice).Status);
        Assert.Equal(JobStatus.Ready, q.Jobs.Single(j => j.Owner == Bob).Status);
    }

    [Fact]
    public void A_drop_matches_only_the_jobs_the_account_sees()
    {
        using var q = NewQueue();
        var video = Video("Ep01.mkv");
        q.AddPaths([video], new JobSettings(), null, null, Bob);
        var sub = Path.Combine(Path.GetDirectoryName(video)!, "Ep01.ita.ass");
        File.WriteAllText(sub, "[Script Info]");

        var bob = DropClassifier.Classify([sub, video], q, visible: j => j.Owner == Bob);
        Assert.Equal(DropKind.Duplicate, bob.Items.Single(i => i.Path == video).Kind);

        // Alice does not see Bob's job: his video is new for her, and a job id of his is ignored.
        var alice = DropClassifier.Classify([video], q, q.Jobs[0].Id, j => j.Owner == Alice);
        Assert.Equal(DropKind.Video, alice.Items.Single().Kind);
        var aliceSub = DropClassifier.Classify([sub], q, q.Jobs[0].Id, j => j.Owner == Alice);
        Assert.Equal(DropKind.Rejected, aliceSub.Items.Single().Kind);
    }

    // ------------------------------------------------------------------ advanced mode

    [Fact]
    public void Options_of_KnAs_own_rigaya_profiles_are_not_hand_written()
    {
        var profile = new JobSettings { Encoder = new EncoderOverrides { Extra = "--aq-temporal --split-enc auto --b-pyramid" } };
        Assert.Empty(AdvancedContent.Of(profile));
        var mixed = new JobSettings { Encoder = new EncoderOverrides { Extra = "--aq-temporal --output-depth 10" } };
        Assert.Equal("--output-depth 10", Assert.Single(AdvancedContent.Of(mixed)).Value);
    }
}
