using System.Security.Cryptography;
using System.Text;
using KnA.Core.Localization;

namespace KnA.Core.Accounts;

/// <summary>
/// Single sign-on through a reverse proxy (Authelia, Authentik, oauth2-proxy, Cloudflare Access…): the proxy signs the
/// person in and sends their user name in a header, which KnA takes as the signed-in account. Read from:
///   KNA_SSO                "authelia" (Remote-User, Remote-Groups), "authentik" (X-authentik-username,
///                          X-authentik-groups) or the name of the header with the user name; unset = off
///   KNA_SSO_GROUPS_HEADER  the header with the groups (comma, "|" or ";" separated), when not the preset's
///   KNA_SSO_ADMIN_GROUP    members are administrators, the others users (kept in step at every request)
///   KNA_SSO_USER_GROUP     only members (and the admin group's) may use KnA
///   KNA_SSO_CREATE         "false": only people with an account already; otherwise accounts are created at first sight
///   KNA_SSO_SECRET         a value the proxy must also send (KNA_SSO_SECRET_HEADER, default X-KnA-Proxy-Secret)
///   KNA_SSO_LOGOUT_URL     where "Sign out" leads (the SSO portal's logout)
/// The headers count only from KNA_TRUSTED_PROXIES (the web server checks that); without them KnA's own sign-in works
/// as before.
/// </summary>
public sealed record SsoConfig
{
    public required string Provider { get; init; }
    public required string UserHeader { get; init; }
    public string? GroupsHeader { get; init; }
    public string? AdminGroup { get; init; }
    public string? UserGroup { get; init; }
    public bool CreateAccounts { get; init; } = true;
    public string? Secret { get; init; }
    public string SecretHeader { get; init; } = "X-KnA-Proxy-Secret";
    public string? LogoutUrl { get; init; }

    /// <summary>The configuration from KNA_SSO_* (<paramref name="env"/> gets a name without "KNA_"); null when off.</summary>
    public static SsoConfig? From(Func<string, string?> env)
    {
        var sso = env("SSO")?.Trim();
        if (string.IsNullOrEmpty(sso) || sso.ToLowerInvariant() is "off" or "false" or "0" or "none") return null;
        var (provider, user, groups) = sso.ToLowerInvariant() switch
        {
            "authelia" => ("Authelia", "Remote-User", "Remote-Groups"),
            "authentik" => ("Authentik", "X-authentik-username", "X-authentik-groups"),
            _ => ("SSO", sso, (string?)null),
        };
        string? Opt(string n) => env(n)?.Trim() is { Length: > 0 } v ? v : null;
        return new SsoConfig
        {
            Provider = provider,
            UserHeader = user,
            GroupsHeader = Opt("SSO_GROUPS_HEADER") ?? groups,
            AdminGroup = Opt("SSO_ADMIN_GROUP"),
            UserGroup = Opt("SSO_USER_GROUP"),
            CreateAccounts = Opt("SSO_CREATE")?.ToLowerInvariant() is not ("false" or "0" or "no" or "off"),
            Secret = Opt("SSO_SECRET"),
            SecretHeader = Opt("SSO_SECRET_HEADER") ?? "X-KnA-Proxy-Secret",
            LogoutUrl = Opt("SSO_LOGOUT_URL"),
        };
    }
}

/// <summary>What the single sign-on headers of a request mean.</summary>
public sealed record SsoResult(Account? Account, string? Error)
{
    /// <summary>No SSO user in the request (or a wrong secret): KnA's own sign-in applies.</summary>
    public static readonly SsoResult None = new(null, null);
}

public sealed class ProxySso(SsoConfig config, AccountStore accounts)
{
    public SsoConfig Config => config;

    /// <summary>
    /// The account of a request that came from a trusted proxy (<paramref name="header"/> reads its headers). An
    /// unknown person gets an account (unless KNA_SSO_CREATE=false); a role follows KNA_SSO_ADMIN_GROUP.
    /// </summary>
    public SsoResult Resolve(Func<string, string?> header)
    {
        if (config.Secret is { } secret && !SameSecret(header(config.SecretHeader), secret)) return SsoResult.None;
        var name = header(config.UserHeader)?.Trim();
        if (string.IsNullOrEmpty(name)) return SsoResult.None;
        // Header values are Latin-1 on the wire: a UTF-8 name ("José") arrives as mojibake; read it back.
        name = FixEncoding(name);

        var groups = Groups(header(config.GroupsHeader ?? "") ?? "");
        bool admin = config.AdminGroup is { } ag && groups.Contains(ag);
        if (config.UserGroup is { } ug && !admin && !groups.Contains(ug))
            return new(null, L.T("{0} lets you in, but you are not in the group \"{1}\" that may use KnA.", config.Provider, ug));

        var account = accounts.Find(name);
        if (account is null)
        {
            if (!config.CreateAccounts)
                return new(null, L.T("{0} signed you in as \"{1}\", but KnA has no account with that name: ask an administrator to create it.", config.Provider, name));
            try { account = accounts.CreateFromSso(name, admin ? AccountRole.Admin : AccountRole.User); }
            catch (ArgumentException ex)
            {
                // Created meanwhile by a parallel request, or a name KnA can't use.
                account = accounts.Find(name);
                if (account is null) return new(null, L.T("{0} signed you in as \"{1}\": {2}", config.Provider, name, ex.Message));
            }
        }
        if (account.Disabled) return new(null, L.T("This account is disabled."));

        if (config.AdminGroup is not null && account.IsAdmin != admin)
        {
            try { account = accounts.Update(account.Id, role: admin ? AccountRole.Admin : AccountRole.User); }
            catch (InvalidOperationException) { /* the last active administrator stays one */ }
        }
        accounts.TouchLogin(account.Id);
        return new(account, null);
    }

    public static HashSet<string> Groups(string value) =>
        value.Split([',', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool SameSecret(string? sent, string expected) =>
        sent is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sent)), SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected)));

    private static string FixEncoding(string value)
    {
        if (value.All(c => c < 128) || value.Any(c => c > 255)) return value;
        try { return new UTF8Encoding(false, true).GetString(System.Text.Encoding.Latin1.GetBytes(value)); }
        catch (DecoderFallbackException) { return value; }
    }
}
