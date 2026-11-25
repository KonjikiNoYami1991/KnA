using KnA.Core.Accounts;
using KnA.Core.Encoding;
using KnA.Core.Media;

namespace KnA.Core.Tests;

public sealed class AccountTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kna-acc-").FullName;
    private DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    static AccountTests() => PasswordHasher.Iterations = 1000;   // the real cost would make the suite slow

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private AccountStore NewStore() => new(_dir, () => _now);

    // ------------------------------------------------------------------ password hashes

    [Fact]
    public void Hash_is_salted_slow_and_verifiable()
    {
        var a = PasswordHasher.Hash("correct horse");
        var b = PasswordHasher.Hash("correct horse");
        Assert.NotEqual(a, b);                                   // random salt
        Assert.StartsWith("pbkdf2-sha256$1000$", a);
        Assert.DoesNotContain("correct horse", a);
        Assert.True(PasswordHasher.Verify("correct horse", a));
        Assert.False(PasswordHasher.Verify("correct horsE", a));
        Assert.False(PasswordHasher.Verify("x", "garbage"));
        Assert.False(PasswordHasher.Verify("x", null));
        Assert.Equal(600_000, PasswordHasher.DefaultIterations);
    }

    [Fact]
    public void Old_hashes_are_upgraded_at_sign_in()
    {
        Assert.True(PasswordHasher.NeedsRehash("pbkdf2-sha256$10$AAAA$AAAA"));
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.Hash("x")));
    }

    // ------------------------------------------------------------------ brute-force throttling

    [Fact]
    public void Throttle_locks_after_free_attempts_and_doubles()
    {
        var t = new LoginThrottle(() => _now);
        var keys = new[] { LoginThrottle.IpKey("10.0.0.9"), LoginThrottle.UserKey("Admin") };
        for (int i = 0; i < LoginThrottle.FreeAttempts; i++)
        {
            Assert.Equal(TimeSpan.Zero, t.RetryAfter(keys));
            t.Failed(keys);
        }
        Assert.Equal(TimeSpan.Zero, t.RetryAfter(keys));         // 5 failures: still free
        t.Failed(keys);
        Assert.Equal(TimeSpan.FromSeconds(2), t.RetryAfter(keys));
        _now += TimeSpan.FromSeconds(3);
        t.Failed(keys);
        Assert.Equal(TimeSpan.FromSeconds(4), t.RetryAfter(keys));
        // The user name is locked from any other address too (distributed guessing).
        Assert.True(t.RetryAfter(LoginThrottle.IpKey("10.0.0.10"), LoginThrottle.UserKey("admin")) > TimeSpan.Zero);
        for (int i = 0; i < 30; i++) t.Failed(keys);
        Assert.Equal(LoginThrottle.MaxLock, t.RetryAfter(keys));
        t.Succeeded(keys);
        Assert.Equal(TimeSpan.Zero, t.RetryAfter(keys));
    }

    [Fact]
    public void Throttle_forgets_old_failures()
    {
        var t = new LoginThrottle(() => _now);
        for (int i = 0; i < 7; i++) t.Failed("ip:x");
        Assert.True(t.RetryAfter("ip:x") > TimeSpan.Zero);
        _now += LoginThrottle.Forget + TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.Zero, t.RetryAfter("ip:x"));
        t.Failed("ip:x");
        Assert.Equal(TimeSpan.Zero, t.RetryAfter("ip:x"));        // the count started again
    }

    // ------------------------------------------------------------------ first start, migration

    [Fact]
    public void First_start_creates_one_administrator_only()
    {
        var s = NewStore();
        Assert.True(s.SetupRequired);
        Assert.Throws<ArgumentException>(() => s.CreateFirstAdmin("admin", "short"));
        var admin = s.CreateFirstAdmin("Mara", "a-long-password");
        Assert.True(admin.IsAdmin);
        Assert.False(s.SetupRequired);
        Assert.Throws<InvalidOperationException>(() => s.CreateFirstAdmin("intruder", "another-password"));
        Assert.Single(s.All);
    }

    [Fact]
    public void Kna_password_becomes_the_admin_and_keeps_old_basic_clients_working()
    {
        var s = NewStore();
        var a = s.EnsureFromEnvironment("old", null);           // the old variable had no length rule
        Assert.NotNull(a);
        Assert.Equal("admin", a!.Name);
        Assert.True(a.FromEnvironment);
        Assert.Null(s.EnsureFromEnvironment("other", null));     // only with no account yet

        Assert.Equal(SignInResult.Ok, s.SignIn("admin", "old", out _));
        // HTTP Basic with any user name, as before ("kna:password" in the installers).
        Assert.Equal(SignInResult.Ok, s.SignInBasic("kna", "old", out var viaBasic));
        Assert.Equal(a.Id, viaBasic!.Id);
        Assert.Equal(SignInResult.Invalid, s.SignInBasic("kna", "wrong", out _));
        // A new password ends the compatibility.
        s.SetPassword(a.Id, "a-brand-new-password");
        Assert.Equal(SignInResult.Invalid, s.SignInBasic("kna", "a-brand-new-password", out _));
        Assert.Equal(SignInResult.Ok, s.SignInBasic("admin", "a-brand-new-password", out _));
        Assert.Equal(SignInResult.Invalid, s.SignInBasic("admin", "old", out _));   // the cached check follows the new hash
    }

    [Fact]
    public void Custom_admin_name_from_environment()
    {
        var s = NewStore();
        Assert.Equal("boss", s.EnsureFromEnvironment("secret", "boss")!.Name);
    }

    // ------------------------------------------------------------------ sessions

    [Fact]
    public void Sessions_slide_expire_and_survive_a_restart_without_storing_tokens()
    {
        var s = NewStore();
        var admin = s.CreateFirstAdmin("admin", "a-long-password");
        var shortToken = s.CreateSession(admin, persistent: false);
        var longToken = s.CreateSession(admin, persistent: true);
        Assert.Equal(admin.Id, s.FromSession(shortToken)!.Id);
        Assert.DoesNotContain(shortToken, File.ReadAllText(s.SessionsPath));
        Assert.DoesNotContain("a-long-password", File.ReadAllText(s.AccountsPath));

        var reopened = NewStore();                                // restart
        Assert.Equal(admin.Id, reopened.FromSession(longToken)!.Id);

        _now += TimeSpan.FromHours(11);
        Assert.NotNull(reopened.FromSession(shortToken));         // used: the idle time starts again
        _now += TimeSpan.FromHours(13);
        Assert.Null(reopened.FromSession(shortToken));
        Assert.NotNull(reopened.FromSession(longToken));          // "remember me": 30 days
        _now += TimeSpan.FromDays(31);
        Assert.Null(reopened.FromSession(longToken));
        Assert.Null(reopened.FromSession("made-up"));
    }

    [Fact]
    public void Password_change_disable_and_logout_end_sessions()
    {
        var s = NewStore();
        var admin = s.CreateFirstAdmin("admin", "a-long-password");
        var user = s.Create("ugo", "user-password", AccountRole.User, null);
        var mine = s.CreateSession(user, false);
        var other = s.CreateSession(user, false);
        s.SetPassword(user.Id, "new-user-password", keepToken: mine);
        Assert.NotNull(s.FromSession(mine));
        Assert.Null(s.FromSession(other));

        s.EndSession(mine);
        Assert.Null(s.FromSession(mine));

        var again = s.CreateSession(user, false);
        s.Update(user.Id, disabled: true);
        Assert.Null(s.FromSession(again));
        Assert.Equal(SignInResult.Disabled, s.SignIn("ugo", "new-user-password", out _));
        Assert.Equal(SignInResult.Invalid, s.SignIn("ugo", "wrong-password", out _));
        Assert.NotNull(s.FromSession(s.CreateSession(admin, false)));
    }

    // ------------------------------------------------------------------ administration

    [Fact]
    public void There_is_always_an_active_administrator()
    {
        var s = NewStore();
        var admin = s.CreateFirstAdmin("admin", "a-long-password");
        Assert.Throws<InvalidOperationException>(() => s.Update(admin.Id, role: AccountRole.User));
        Assert.Throws<InvalidOperationException>(() => s.Update(admin.Id, disabled: true));
        Assert.Throws<InvalidOperationException>(() => s.Delete(admin.Id));
        var second = s.Create("second", "another-password", AccountRole.Admin, null);
        s.Update(admin.Id, role: AccountRole.User);                // fine now
        Assert.Throws<InvalidOperationException>(() => s.Delete(second.Id));
        s.Delete(admin.Id);
        Assert.Single(s.All);
    }

    [Fact]
    public void Names_are_unique_and_permissions_are_kept()
    {
        var s = NewStore();
        s.CreateFirstAdmin("admin", "a-long-password");
        Assert.Throws<ArgumentException>(() => s.Create("ADMIN", "a-long-password", AccountRole.User, null));
        Assert.Throws<ArgumentException>(() => s.Create("bad name", "a-long-password", AccountRole.User, null));
        Assert.Throws<ArgumentException>(() => s.Create("", "a-long-password", AccountRole.User, null));
        var u = s.Create("lia", "a-long-password", AccountRole.User, new AccountPermissions { Roots = ["/srv/a", " ", "/srv/a"], Advanced = true });
        Assert.Equal(["/srv/a"], u.Permissions.Roots!);
        Assert.True(u.Permissions.Advanced);
        Assert.Equal(SignInResult.Ok, s.SignIn("LIA", "a-long-password", out _));   // names ignore case
        var renamed = s.Update(u.Id, name: "lia2", permissions: new AccountPermissions { Roots = [] });
        Assert.Null(renamed.Permissions.Roots);                   // empty list = every root
        Assert.False(NewStore().Find("lia2")!.Permissions.Advanced);
    }

    [Fact]
    public void Command_line_reset_recovers_a_lost_administrator()
    {
        var s = NewStore();
        var admin = s.CreateFirstAdmin("admin", "a-long-password");
        s.Create("ops", "ops-password", AccountRole.Admin, null);
        s.Update(admin.Id, disabled: true);
        var reset = s.ResetPassword("admin", "recovered-password");
        Assert.False(reset.Disabled);
        Assert.Equal(SignInResult.Ok, s.SignIn("admin", "recovered-password", out _));
        Assert.True(s.ResetPassword("newboss", "new-boss-password").IsAdmin);
    }

    [Fact]
    public void A_reset_from_the_command_line_reaches_the_running_server()
    {
        var running = NewStore();
        var admin = running.CreateFirstAdmin("admin", "a-long-password");
        var token = running.CreateSession(admin, false);
        Thread.Sleep(20);                                         // a different modification time
        NewStore().ResetPassword("admin", "recovered-password");  // another process
        Assert.Equal(SignInResult.Ok, running.SignIn("admin", "recovered-password", out _));
        Assert.Null(running.FromSession(token));                  // the reset signed it out
        running.Create("next", "next-password", AccountRole.User, null);
        Assert.Equal(SignInResult.Ok, NewStore().SignIn("admin", "recovered-password", out _));   // not overwritten
    }

    [Fact]
    public void A_broken_accounts_file_never_opens_the_server()
    {
        File.WriteAllText(Path.Combine(_dir, "accounts.json"), "{ not json");
        Assert.ThrowsAny<Exception>(() => NewStore());
    }

    [Fact]
    public void Account_files_are_private()
    {
        if (OperatingSystem.IsWindows()) return;
        var s = NewStore();
        s.CreateFirstAdmin("admin", "a-long-password");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(s.AccountsPath));
    }

    // ------------------------------------------------------------------ advanced content

    [Fact]
    public void Advanced_content_is_scripts_and_hand_written_arguments()
    {
        var plain = new JobSettings();
        Assert.Empty(AdvancedContent.Of(plain));
        var catalogue = plain with { Frameserver = new FrameserverSettings { Kind = FrameserverKind.VapourSynth, Chain = [new("deband")], Script = "ignored" } };
        Assert.Empty(AdvancedContent.Of(catalogue));
        var script = plain with { Frameserver = new FrameserverSettings { Kind = FrameserverKind.VapourSynth, Script = "import os; os.system('x')" } };
        Assert.Single(AdvancedContent.Of(script));
        Assert.Single(AdvancedContent.Of(plain with { Encoder = new EncoderOverrides { Extra = "-x265-params aq-mode=3" } }));
        Assert.Single(AdvancedContent.Of(plain with { VideoFilters = new VideoFilterSettings { Custom = "eq=gamma=1.1" } }));
        Assert.Single(AdvancedContent.Of(plain with { Arguments = new ToolArguments { Muxer = "--no-chapters" } }));
        Assert.Single(AdvancedContent.Of(plain with { Upscale = new UpscaleSettings { Model = "/tmp/evil.pth" } }));
        Assert.Empty(AdvancedContent.Of(plain with { Upscale = new UpscaleSettings { Model = "realesr-animevideov3" } }));
        // rigaya's GPU filters are a normal feature; other encoder options among them are not.
        Assert.Empty(AdvancedContent.Of(plain with { Encoder = new EncoderOverrides { GpuFilters = "--vpp-resize spline36 --vpp-nlmeans sigma=0.005 --vpp-deband -1" } }));
        Assert.Single(AdvancedContent.Of(plain with { Encoder = new EncoderOverrides { GpuFilters = "--vpp-resize spline36 --log /etc/x" } }));
        var edit = new JobEdit { Tracks = [new TrackPlan { File = "a.mkv", Kind = "audio", Arguments = "-cutoff 20000" }] };
        Assert.Single(AdvancedContent.Of(plain, edit));
        Assert.True(AdvancedContent.IsScriptSource("/media/x.vpy"));
        Assert.False(AdvancedContent.IsScriptSource("/media/x.mkv"));
    }

    [Fact]
    public void Advanced_content_already_there_may_be_kept()
    {
        var preset = new JobSettings { Encoder = new EncoderOverrides { Extra = "-tune animation" } };
        var allowed = AdvancedContent.Of(preset);
        Assert.Null(AdvancedContent.FirstNew(preset, null, allowed));
        Assert.Null(AdvancedContent.FirstNew(new JobSettings(), null, []));
        var changed = preset with { Encoder = preset.Encoder with { Extra = "-tune animation -y /etc/passwd" } };
        var item = AdvancedContent.FirstNew(changed, null, allowed);
        Assert.NotNull(item);
        Assert.Contains("advanced mode", AdvancedContent.Denied(item!));
    }
}
