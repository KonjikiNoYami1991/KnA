using KnA.Core.Accounts;

namespace KnA.Core.Tests;

/// <summary>Single sign-on through a reverse proxy (Authelia / Authentik headers).</summary>
public sealed class ProxySsoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kna-sso-").FullName;

    static ProxySsoTests() => PasswordHasher.Iterations = 1000;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        n => vars.FirstOrDefault(v => v.Name == n).Value;

    private static Func<string, string?> Headers(params (string Name, string Value)[] h) =>
        n => h.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Value;

    private (ProxySso Sso, AccountStore Accounts) New(params (string, string)[] env)
    {
        var accounts = new AccountStore(_dir);
        return (new ProxySso(SsoConfig.From(Env(env))!, accounts), accounts);
    }

    [Fact]
    public void Presets_and_custom_headers()
    {
        Assert.Null(SsoConfig.From(Env()));
        Assert.Null(SsoConfig.From(Env(("SSO", "off"))));
        var authelia = SsoConfig.From(Env(("SSO", "Authelia")))!;
        Assert.Equal(("Remote-User", "Remote-Groups"), (authelia.UserHeader, authelia.GroupsHeader));
        var authentik = SsoConfig.From(Env(("SSO", "authentik")))!;
        Assert.Equal(("X-authentik-username", "X-authentik-groups"), (authentik.UserHeader, authentik.GroupsHeader));
        var custom = SsoConfig.From(Env(("SSO", "X-Auth-Request-User"), ("SSO_CREATE", "false"), ("SSO_GROUPS_HEADER", "X-Auth-Request-Groups")))!;
        Assert.Equal("X-Auth-Request-User", custom.UserHeader);
        Assert.Equal("X-Auth-Request-Groups", custom.GroupsHeader);
        Assert.False(custom.CreateAccounts);
        Assert.Equal(["a", "b", "c"], ProxySso.Groups("a, b|c;").Order());
    }

    [Fact]
    public void Without_the_header_KnAs_own_sign_in_applies()
    {
        var (sso, _) = New(("SSO", "authelia"));
        Assert.Same(SsoResult.None, sso.Resolve(Headers()));
        Assert.Same(SsoResult.None, sso.Resolve(Headers(("Remote-User", "  "))));
    }

    [Fact]
    public void Accounts_are_created_at_first_sight_the_first_one_administrator()
    {
        var (sso, accounts) = New(("SSO", "authelia"));
        var first = sso.Resolve(Headers(("Remote-User", "alice"))).Account!;
        Assert.True(first.IsAdmin);              // no account yet: the first person creates the administrator
        Assert.True(first.FromSso);
        var second = sso.Resolve(Headers(("Remote-User", "bob"))).Account!;
        Assert.False(second.IsAdmin);
        Assert.False(second.Permissions.Advanced);
        Assert.Equal(second.Id, sso.Resolve(Headers(("Remote-User", "BOB"))).Account!.Id);   // names ignore case
        Assert.Equal(2, accounts.All.Count);
        // The random password is nobody's: KnA's sign-in with a guess fails.
        Assert.Equal(SignInResult.Invalid, accounts.SignIn("bob", "", out _));
    }

    [Fact]
    public void Roles_follow_the_admin_group_and_the_user_group_lets_people_in()
    {
        var (sso, accounts) = New(("SSO", "authentik"), ("SSO_ADMIN_GROUP", "kna-admins"), ("SSO_USER_GROUP", "kna"));
        accounts.Create("root", "rootpass1", AccountRole.Admin, null);
        var carol = sso.Resolve(Headers(("X-authentik-username", "carol"), ("X-authentik-groups", "family|kna-admins"))).Account!;
        Assert.True(carol.IsAdmin);
        carol = sso.Resolve(Headers(("X-authentik-username", "carol"), ("X-authentik-groups", "family|kna"))).Account!;
        Assert.False(carol.IsAdmin);             // left the admin group: a user again
        var denied = sso.Resolve(Headers(("X-authentik-username", "dave"), ("X-authentik-groups", "family")));
        Assert.Null(denied.Account);
        Assert.Contains("kna", denied.Error);
        Assert.Null(accounts.Find("dave"));
    }

    [Fact]
    public void The_last_administrator_stays_one()
    {
        var (sso, _) = New(("SSO", "authelia"), ("SSO_ADMIN_GROUP", "admins"));
        var only = sso.Resolve(Headers(("Remote-User", "alice"), ("Remote-Groups", "admins"))).Account!;
        Assert.True(only.IsAdmin);
        Assert.True(sso.Resolve(Headers(("Remote-User", "alice"), ("Remote-Groups", "users"))).Account!.IsAdmin);
    }

    [Fact]
    public void Without_creation_only_existing_enabled_accounts()
    {
        var (sso, accounts) = New(("SSO", "authelia"), ("SSO_CREATE", "false"));
        Assert.Contains("no account", sso.Resolve(Headers(("Remote-User", "eve"))).Error);
        var frank = accounts.Create("frank", "frankpass1", AccountRole.User, null);
        Assert.Equal(frank.Id, sso.Resolve(Headers(("Remote-User", "frank"))).Account!.Id);
        accounts.Update(frank.Id, disabled: true);
        Assert.NotNull(sso.Resolve(Headers(("Remote-User", "frank"))).Error);
        Assert.NotNull(sso.Resolve(Headers(("Remote-User", "bad name!"))).Error);
    }

    [Fact]
    public void The_shared_secret_must_come_with_the_header()
    {
        var (sso, _) = New(("SSO", "authelia"), ("SSO_SECRET", "s3cret"));
        Assert.Same(SsoResult.None, sso.Resolve(Headers(("Remote-User", "alice"))));
        Assert.Same(SsoResult.None, sso.Resolve(Headers(("Remote-User", "alice"), ("X-KnA-Proxy-Secret", "wrong"))));
        Assert.NotNull(sso.Resolve(Headers(("Remote-User", "alice"), ("X-KnA-Proxy-Secret", "s3cret"))).Account);
    }

    [Fact]
    public void UTF8_names_sent_as_Latin1_are_read_back()
    {
        var (sso, _) = New(("SSO", "authelia"));
        var latin1 = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes("josé"));
        Assert.Equal("josé", sso.Resolve(Headers(("Remote-User", latin1))).Account!.Name);
    }
}
