using System.Collections.Concurrent;
using System.Net;
using System.Text;
using KnA.Core.Accounts;
using KnA.Core.Localization;
using KnA.Core.Settings;

namespace KnA.Server;

/// <summary>Who is making the request: an account, or everybody when accounts are switched off (KNA_AUTH=off).</summary>
public sealed record AppUser(Guid Id, string Name, bool IsAdmin, AccountPermissions Permissions, bool AuthDisabled = false)
{
    public static AppUser From(Account a) => new(a.Id, a.Name, a.IsAdmin, a.Permissions);
    public static readonly AppUser Everybody = new(Guid.Empty, "admin", true, new AccountPermissions(), AuthDisabled: true);
    /// <summary>Requests from this machine before the first account exists (the installers set up components).</summary>
    public static readonly AppUser LocalSetup = new(Guid.Empty, "setup", true, new AccountPermissions());
    public bool MayUseAdvanced => IsAdmin || Permissions.Advanced;

    /// <summary>The account its new jobs belong to (null: accounts switched off, set-up from this machine).</summary>
    public Guid? OwnerId => Id == Guid.Empty ? null : Id;

    /// <summary>
    /// A user keeps their own presets and settings; administrators (and everybody, with accounts switched off) save
    /// into the shared ones, which every account sees.
    /// </summary>
    public bool HasOwnData => !IsAdmin && Id != Guid.Empty;

    /// <summary>Users see and manage their own jobs; administrators see and manage everybody's.</summary>
    public bool Sees(KnA.Core.Queue.QueueJob job) => IsAdmin || job.Owner == Id;
}

public sealed record LoginRequest(string Name, string Password, bool Remember = false);
public sealed record PasswordChangeRequest(string Current, string Password);
public sealed record AccountCreateRequest(string Name, string Password, AccountRole Role = AccountRole.User, AccountPermissions? Permissions = null, bool Disabled = false);
public sealed record AccountUpdateRequest(string? Name = null, AccountRole? Role = null, bool? Disabled = null, AccountPermissions? Permissions = null, string? Password = null);

/// <summary>
/// Sign-in for the web UI and the API: user accounts (admin / user, per-user folders and advanced mode) instead of the
/// old shared password. Browsers get an HttpOnly, SameSite=Strict session cookie; scripts may keep using HTTP Basic
/// with an account's name and password. Failed sign-ins are throttled per address and per user name.
///   KNA_PASSWORD     with no account yet, creates the administrator (KNA_ADMIN_USER, default "admin") with this password
///   KNA_AUTH=off     no accounts at all: everybody is an administrator (trusted networks only)
/// </summary>
public static class Auth
{
    public const string CookieName = "kna_session";
    private const string UserKey = "kna.user";

    // The sign-in screen and its texts must work before anybody signs in.
    private static readonly string[] Anonymous = ["/api/auth/status", "/api/auth/login", "/api/auth/setup", "/api/auth/logout", "/api/i18n"];

    public static bool Disabled => AppEnv.Get("AUTH")?.Trim().ToLowerInvariant() is "off" or "none" or "false" or "0" or "disabled";

    public static AppUser CurrentUser(this HttpContext ctx) =>
        ctx.Items.TryGetValue(UserKey, out var u) && u is AppUser user ? user : Disabled ? AppUser.Everybody : throw new UnauthorizedAccessException();

    /// <summary>Only administrators may call this endpoint.</summary>
    public static TBuilder AdminOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.CurrentUser().IsAdmin ? await next(ctx)
                : Results.Problem(L.T("Only an administrator can do this."), statusCode: 403));

    public static void UseAccounts(this WebApplication app, AccountStore accounts, LoginThrottle throttle)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            if (Disabled || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                if (Disabled) ctx.Items[UserKey] = AppUser.Everybody;
                await next();
                return;
            }
            if (Resolve(ctx, accounts, throttle, out var failure) is { } user)
            {
                ctx.Items[UserKey] = user;
                await next();
                return;
            }
            if (Anonymous.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase)))
            {
                await next();
                return;
            }
            await failure(ctx);
        });
    }

    private static AppUser? Resolve(HttpContext ctx, AccountStore accounts, LoginThrottle throttle, out Func<HttpContext, Task> failure)
    {
        failure = Unauthorized(accounts);
        if (ctx.Request.Cookies[CookieName] is { Length: > 0 } token && accounts.FromSession(token) is { } fromCookie)
            return AppUser.From(fromCookie);

        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string name, password;
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon < 0) return null;
                (name, password) = (decoded[..colon], decoded[(colon + 1)..]);
            }
            catch (FormatException) { return null; }
            var keys = new[] { LoginThrottle.IpKey(ClientAddress(ctx)), LoginThrottle.UserKey(name) };
            var wait = throttle.RetryAfter(keys);
            if (wait > TimeSpan.Zero) { failure = TooMany(wait); return null; }
            switch (accounts.SignInBasic(name, password, out var account))
            {
                case SignInResult.Ok:
                    throttle.Succeeded(keys);
                    return AppUser.From(account!);
                case SignInResult.Disabled:
                    failure = Problem(L.T("This account is disabled."), 403);
                    return null;
                default:
                    throttle.Failed(keys);
                    return null;
            }
        }

        // Before the first account exists, this machine itself (an installer setting up components) may use the API;
        // anybody else sees the "create the administrator" screen.
        if (accounts.SetupRequired && IsLoopback(ctx)) return AppUser.LocalSetup;
        return null;
    }

    public static void MapAuth(this WebApplication app, AccountStore accounts, LoginThrottle throttle, Func<AppUser, FileBrowser> browserFor, Action<Guid>? deleted = null)
    {
        app.MapGet("/api/auth/status", (HttpContext ctx) =>
        {
            AppUser? user = ctx.Items.TryGetValue(UserKey, out var u) ? u as AppUser : null;
            return Results.Ok(new
            {
                authDisabled = Disabled,
                setupRequired = !Disabled && accounts.SetupRequired,
                user = user is null || user == AppUser.LocalSetup ? null : Describe(user, browserFor(user)),
                minPasswordLength = AccountStore.MinPasswordLength,
            });
        });

        app.MapPost("/api/auth/setup", (LoginRequest req, HttpContext ctx) =>
        {
            if (Disabled) return Results.Problem(L.T("Accounts are switched off on this server (KNA_AUTH)."), statusCode: 400);
            var account = accounts.CreateFirstAdmin(req.Name, req.Password);
            app.Logger.LogInformation("Administrator account \"{Name}\" created from {Address}.", account.Name, ClientAddress(ctx));
            SetCookie(ctx, accounts.CreateSession(account, req.Remember), req.Remember);
            return Results.Ok(new { user = Describe(AppUser.From(account), browserFor(AppUser.From(account))) });
        });

        app.MapPost("/api/auth/login", async (LoginRequest req, HttpContext ctx) =>
        {
            if (Disabled) return Results.Ok();
            var keys = new[] { LoginThrottle.IpKey(ClientAddress(ctx)), LoginThrottle.UserKey(req.Name) };
            var wait = throttle.RetryAfter(keys);
            if (wait > TimeSpan.Zero)
            {
                await TooMany(wait)(ctx);
                return Results.Empty;
            }
            switch (accounts.SignIn(req.Name ?? "", req.Password ?? "", out var account))
            {
                case SignInResult.Ok:
                    throttle.Succeeded(keys);
                    SetCookie(ctx, accounts.CreateSession(account!, req.Remember), req.Remember);
                    return Results.Ok(new { user = Describe(AppUser.From(account!), browserFor(AppUser.From(account!))) });
                case SignInResult.Disabled:
                    return Results.Problem(L.T("This account is disabled."), statusCode: 403);
                default:
                    throttle.Failed(keys);
                    app.Logger.LogWarning("Failed sign-in for \"{Name}\" from {Address}.", req.Name, ClientAddress(ctx));
                    return Results.Problem(L.T("Wrong user name or password."), statusCode: 401);
            }
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            accounts.EndSession(ctx.Request.Cookies[CookieName]);
            ctx.Response.Cookies.Delete(CookieName, CookieOptions(ctx, false));
            return Results.Ok();
        });

        app.MapPost("/api/auth/password", (PasswordChangeRequest req, HttpContext ctx) =>
        {
            var user = ctx.CurrentUser();
            if (accounts.Find(user.Id) is not { } account) return Results.Problem(L.T("Accounts are switched off on this server (KNA_AUTH)."), statusCode: 400);
            if (!PasswordHasher.Verify(req.Current ?? "", account.PasswordHash))
                return Results.Problem(L.T("The current password is wrong."), statusCode: 400);
            accounts.SetPassword(account.Id, req.Password, ctx.Request.Cookies[CookieName]);
            return Results.Ok();
        });

        // ------------------------------------------------------------ administrators: the accounts
        app.MapGet("/api/accounts", () => accounts.All).AdminOnly();
        app.MapPost("/api/accounts", (AccountCreateRequest req) =>
        {
            var a = accounts.Create(req.Name, req.Password, req.Role, req.Permissions);
            if (req.Disabled) a = accounts.Update(a.Id, disabled: true);
            return Results.Ok(accounts.View(a));
        }).AdminOnly();
        app.MapPut("/api/accounts/{id:guid}", (Guid id, AccountUpdateRequest req, HttpContext ctx) =>
        {
            if (req.Password is { Length: > 0 } pw) accounts.SetPassword(id, pw, id == ctx.CurrentUser().Id ? ctx.Request.Cookies[CookieName] : null);
            var a = accounts.Update(id, req.Name, req.Role, req.Disabled, req.Permissions);
            return Results.Ok(accounts.View(a));
        }).AdminOnly();
        app.MapPost("/api/accounts/{id:guid}/signout", (Guid id) => { accounts.EndSessions(id); return Results.Ok(); }).AdminOnly();
        app.MapDelete("/api/accounts/{id:guid}", (Guid id) => { accounts.Delete(id); deleted?.Invoke(id); return Results.Ok(); }).AdminOnly();
    }

    private static object Describe(AppUser u, FileBrowser browser) => new
    {
        u.Id, u.Name, u.IsAdmin, advanced = u.MayUseAdvanced, roots = browser.Roots, u.AuthDisabled,
    };

    private static void SetCookie(HttpContext ctx, string token, bool persistent) =>
        ctx.Response.Cookies.Append(CookieName, token, CookieOptions(ctx, persistent));

    private static CookieOptions CookieOptions(HttpContext ctx, bool persistent) => new()
    {
        HttpOnly = true,
        Secure = ctx.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        Expires = persistent ? DateTimeOffset.UtcNow + AccountStore.RememberedSessionIdle : null,
    };

    private static Func<HttpContext, Task> Unauthorized(AccountStore accounts) => ctx =>
    {
        ctx.Response.StatusCode = 401;
        return ctx.Response.WriteAsJsonAsync(new { title = L.T("Sign in to KnA."), detail = L.T("Sign in to KnA."), setupRequired = accounts.SetupRequired });
    };

    private static Func<HttpContext, Task> TooMany(TimeSpan wait) => ctx =>
    {
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        ctx.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ctx.Response.StatusCode = 429;
        var text = L.T("Too many failed sign-ins: try again in {0} s.", seconds);
        return ctx.Response.WriteAsJsonAsync(new { title = text, detail = text });
    };

    private static Func<HttpContext, Task> Problem(string text, int status) => ctx =>
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { title = text, detail = text });
    };

    public static string ClientAddress(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

    private static bool IsLoopback(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
}

/// <summary>Each account's view of the server's folders (<see cref="AccountPermissions.Roots"/>), cached.</summary>
public sealed class UserBrowsers(FileBrowser server)
{
    private readonly ConcurrentDictionary<string, FileBrowser> _cache = new(StringComparer.Ordinal);

    public FileBrowser For(AppUser user)
    {
        if (user.IsAdmin || user.Permissions.Roots is not { Count: > 0 } roots) return server;
        var key = user.Id + "|" + string.Join('\n', roots);
        if (_cache.Count > 500) _cache.Clear();
        return _cache.GetOrAdd(key, _ => server.Restrict(roots));
    }
}
