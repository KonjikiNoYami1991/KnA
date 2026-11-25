using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnA.Core.Localization;
using KnA.Core.Settings;

namespace KnA.Core.Accounts;

public enum AccountRole { User, Admin }

/// <summary>What a user (not an administrator: they may do everything) is allowed to do.</summary>
public sealed record AccountPermissions
{
    /// <summary>Folders this user may browse and encode from (inside the server's media roots); null = all of them.</summary>
    public List<string>? Roots { get; init; }
    /// <summary>
    /// Hand-written code and arguments that run on the server: VapourSynth / AviSynth scripts, extra encoder and tool
    /// arguments, custom FFmpeg filters. Off for new users.
    /// </summary>
    public bool Advanced { get; init; }
}

public sealed record Account
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public AccountRole Role { get; init; }
    public string PasswordHash { get; init; } = "";
    public bool Disabled { get; init; }
    public AccountPermissions Permissions { get; init; } = new();
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLogin { get; init; }
    /// <summary>
    /// Created from KNA_PASSWORD by an older install: HTTP Basic with any user name and that password still signs
    /// in as this account (scripts written for the shared password keep working) until its password is changed.
    /// </summary>
    public bool FromEnvironment { get; init; }

    [JsonIgnore] public bool IsAdmin => Role == AccountRole.Admin;
}

/// <summary>An account as the browser sees it (never the password hash).</summary>
public sealed record AccountView(Guid Id, string Name, AccountRole Role, bool Disabled, AccountPermissions Permissions,
    DateTimeOffset Created, DateTimeOffset? LastLogin, bool FromEnvironment, int Sessions)
{
    public bool IsAdmin => Role == AccountRole.Admin;
}

public enum SignInResult { Ok, Invalid, Disabled }

/// <summary>
/// The web server's user accounts and sign-in sessions: accounts.json and sessions.json in the data folder, readable
/// only by the service user. Passwords are stored as salted slow hashes (<see cref="PasswordHasher"/>); a session is a
/// random token in an HttpOnly cookie, of which only the SHA-256 is stored, so the files give nobody a way in.
/// </summary>
public sealed class AccountStore
{
    public const int MinPasswordLength = 8;
    public static readonly TimeSpan SessionIdle = TimeSpan.FromHours(12);
    public static readonly TimeSpan RememberedSessionIdle = TimeSpan.FromDays(30);

    private sealed record Session(string TokenHash, Guid AccountId, DateTimeOffset Created, DateTimeOffset LastSeen, bool Persistent);
    private sealed record AccountsFile(int Version, List<Account> Accounts);
    private sealed record SessionsFile(List<Session> Sessions);

    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now;
    private List<Account> _accounts = [];
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private DateTimeOffset _sessionsSaved;
    // accounts.json as last read or written: a change by another process (kna-server --reset-password while the service
    // runs) is read again instead of being overwritten.
    private DateTime _accountsStamp;
    // HTTP Basic sends the password with every request: a successful check is remembered for a few minutes (keyed by
    // an HMAC of name and password with a per-process key, bound to the stored hash) instead of hashing it each time.
    private readonly byte[] _cacheKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, (Guid Id, string Hash, DateTimeOffset Until)> _basicCache = new(StringComparer.Ordinal);

    public string AccountsPath { get; }
    public string SessionsPath { get; }

    public AccountStore(string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(dataDirectory);
        AccountsPath = Path.Combine(dataDirectory, "accounts.json");
        SessionsPath = Path.Combine(dataDirectory, "sessions.json");
        Load();
    }

    // ------------------------------------------------------------------ reading

    /// <summary>No account exists yet: the first person to open KnA creates the administrator.</summary>
    public bool SetupRequired { get { lock (_lock) { Refresh(); return _accounts.Count == 0; } } }

    public IReadOnlyList<AccountView> All
    {
        get
        {
            lock (_lock)
            {
                Refresh();
                return _accounts.OrderByDescending(a => a.IsAdmin).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(View).ToList();
            }
        }
    }

    public Account? Find(Guid id) { lock (_lock) { Refresh(); return _accounts.FirstOrDefault(a => a.Id == id); } }
    public Account? Find(string name) { lock (_lock) { Refresh(); return FindByName(name); } }

    public AccountView View(Account a)
    {
        lock (_lock)
            return new(a.Id, a.Name, a.Role, a.Disabled, a.Permissions, a.Created, a.LastLogin, a.FromEnvironment, _sessions.Values.Count(s => s.AccountId == a.Id));
    }

    // ------------------------------------------------------------------ first start and KNA_PASSWORD

    /// <summary>Creates the first administrator; fails once any account exists (only one person wins the race).</summary>
    public Account CreateFirstAdmin(string name, string password)
    {
        lock (_lock)
        {
            if (_accounts.Count > 0) throw new InvalidOperationException(L.T("The administrator account already exists: sign in."));
            var admin = AddLocked(name, password, AccountRole.Admin, new AccountPermissions(), fromEnvironment: false, checkLength: true) with { LastLogin = _now() };
            Replace(admin);
            SaveAccounts();
            return admin;
        }
    }

    /// <summary>
    /// Migration of the old shared password (KNA_PASSWORD, HTTP Basic with any user name): with no account yet, it
    /// becomes the password of an administrator named <paramref name="name"/> ("admin"). Returns the account created,
    /// null when accounts already exist (the variable is then not used any more).
    /// </summary>
    public Account? EnsureFromEnvironment(string? password, string? name)
    {
        if (string.IsNullOrEmpty(password)) return null;
        lock (_lock)
        {
            if (_accounts.Count > 0) return null;
            return AddLocked(string.IsNullOrWhiteSpace(name) ? "admin" : name.Trim(), password, AccountRole.Admin, new AccountPermissions(),
                fromEnvironment: true, checkLength: false);
        }
    }

    // ------------------------------------------------------------------ signing in

    public SignInResult SignIn(string name, string password, out Account? account)
    {
        account = Find(name);
        if (account is null || !PasswordHasher.Verify(password, account.PasswordHash))
        {
            if (account is null) PasswordHasher.VerifyDummy(password);
            account = null;
            return SignInResult.Invalid;
        }
        if (account.Disabled) return SignInResult.Disabled;
        lock (_lock)
        {
            var updated = account with { LastLogin = _now() };
            if (PasswordHasher.NeedsRehash(account.PasswordHash)) updated = updated with { PasswordHash = PasswordHasher.Hash(password) };
            Replace(updated);
            SaveAccounts();
            account = updated;
        }
        return SignInResult.Ok;
    }

    /// <summary>
    /// HTTP Basic (scripts, curl -u): the account with that name; an unknown name falls back to the account migrated
    /// from KNA_PASSWORD, which accepted any user name.
    /// </summary>
    public SignInResult SignInBasic(string name, string password, out Account? account)
    {
        var cacheKey = Convert.ToHexString(HMACSHA256.HashData(_cacheKey, System.Text.Encoding.UTF8.GetBytes(name + "\0" + password)));
        if (_basicCache.TryGetValue(cacheKey, out var hit) && hit.Until > _now() && Find(hit.Id) is { } cached && cached.PasswordHash == hit.Hash)
        {
            account = cached;
            return cached.Disabled ? SignInResult.Disabled : SignInResult.Ok;
        }
        Account? target = Find(name);
        if (target is null) lock (_lock) target = _accounts.FirstOrDefault(a => a.FromEnvironment);
        if (target is null || !PasswordHasher.Verify(password, target.PasswordHash))
        {
            if (target is null) PasswordHasher.VerifyDummy(password);
            account = null;
            return SignInResult.Invalid;
        }
        account = target;
        if (target.Disabled) return SignInResult.Disabled;
        _basicCache[cacheKey] = (target.Id, target.PasswordHash, _now() + TimeSpan.FromMinutes(5));
        if (_basicCache.Count > 1000) _basicCache.Clear();
        return SignInResult.Ok;
    }

    /// <summary>A new session for <paramref name="account"/>; returns the token for the cookie.</summary>
    public string CreateSession(Account account, bool persistent)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_lock)
        {
            var now = _now();
            _sessions[HashToken(token)] = new Session(HashToken(token), account.Id, now, now, persistent);
            SaveSessions(force: true);
        }
        return token;
    }

    /// <summary>The account signed in with this session token (sliding expiry); null when unknown, expired or disabled.</summary>
    public Account? FromSession(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128) return null;
        lock (_lock)
        {
            Refresh();
            var key = HashToken(token);
            if (!_sessions.TryGetValue(key, out var s)) return null;
            var now = _now();
            var account = _accounts.FirstOrDefault(a => a.Id == s.AccountId);
            if (account is null || account.Disabled || now - s.LastSeen > (s.Persistent ? RememberedSessionIdle : SessionIdle))
            {
                _sessions.Remove(key);
                SaveSessions(force: true);
                return null;
            }
            if (now - s.LastSeen > TimeSpan.FromMinutes(1))
            {
                _sessions[key] = s with { LastSeen = now };
                SaveSessions(force: false);
            }
            return account;
        }
    }

    public void EndSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        lock (_lock)
            if (_sessions.Remove(HashToken(token))) SaveSessions(force: true);
    }

    /// <summary>Signs the account out everywhere (except the session <paramref name="keepToken"/>, if given).</summary>
    public void EndSessions(Guid accountId, string? keepToken = null)
    {
        lock (_lock)
        {
            var keep = keepToken is null ? null : HashToken(keepToken);
            foreach (var k in _sessions.Where(kv => kv.Value.AccountId == accountId && kv.Key != keep).Select(kv => kv.Key).ToList())
                _sessions.Remove(k);
            SaveSessions(force: true);
        }
    }

    // ------------------------------------------------------------------ managing accounts (administrators)

    public Account Create(string name, string password, AccountRole role, AccountPermissions? permissions)
    {
        lock (_lock) return AddLocked(name, password, role, permissions ?? new AccountPermissions(), fromEnvironment: false, checkLength: true);
    }

    /// <summary>Changes name, role, permissions or the disabled state; the last enabled administrator always stays one.</summary>
    public Account Update(Guid id, string? name = null, AccountRole? role = null, bool? disabled = null, AccountPermissions? permissions = null)
    {
        lock (_lock)
        {
            Refresh();
            var a = _accounts.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(L.T("Unknown account."));
            var updated = a with
            {
                Name = name is null ? a.Name : CheckName(name, a.Id),
                Role = role ?? a.Role,
                Disabled = disabled ?? a.Disabled,
                Permissions = permissions is null ? a.Permissions : Normalize(permissions),
            };
            if (a.IsAdmin && !a.Disabled && (!updated.IsAdmin || updated.Disabled) && ActiveAdmins() == 1)
                throw new InvalidOperationException(L.T("KnA needs at least one active administrator."));
            Replace(updated);
            SaveAccounts();
            if (updated.Disabled) EndSessions(id);
            return updated;
        }
    }

    /// <summary>New password; every other session of the account is signed out.</summary>
    public Account SetPassword(Guid id, string password, string? keepToken = null)
    {
        CheckPassword(password);
        lock (_lock)
        {
            Refresh();
            var a = _accounts.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(L.T("Unknown account."));
            var updated = a with { PasswordHash = PasswordHasher.Hash(password), FromEnvironment = false };
            Replace(updated);
            SaveAccounts();
            EndSessions(id, keepToken);
            return updated;
        }
    }

    public void Delete(Guid id)
    {
        lock (_lock)
        {
            Refresh();
            var a = _accounts.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException(L.T("Unknown account."));
            if (a.IsAdmin && !a.Disabled && ActiveAdmins() == 1)
                throw new InvalidOperationException(L.T("KnA needs at least one active administrator."));
            _accounts.Remove(a);
            SaveAccounts();
            EndSessions(id);
        }
    }

    /// <summary>
    /// Recovery from the command line (kna-server --reset-password NAME): sets the password and re-enables the account;
    /// an unknown name becomes a new administrator. Every session of the account is signed out.
    /// </summary>
    public Account ResetPassword(string name, string password)
    {
        lock (_lock)
        {
            if (FindByName(name) is not { } a)
                return AddLocked(name, password, AccountRole.Admin, new AccountPermissions(), fromEnvironment: false, checkLength: true);
            SetPassword(a.Id, password);
            return Update(a.Id, disabled: false);
        }
    }

    // ------------------------------------------------------------------ helpers

    private Account AddLocked(string name, string password, AccountRole role, AccountPermissions permissions, bool fromEnvironment, bool checkLength)
    {
        Refresh();
        var clean = CheckName(name, null);
        if (checkLength) CheckPassword(password);
        else if (string.IsNullOrEmpty(password)) throw new ArgumentException(L.T("The password cannot be empty."));
        var a = new Account
        {
            Name = clean, Role = role, PasswordHash = PasswordHasher.Hash(password), Permissions = Normalize(permissions),
            Created = _now(), FromEnvironment = fromEnvironment,
        };
        _accounts.Add(a);
        SaveAccounts();
        return a;
    }

    private string CheckName(string name, Guid? self)
    {
        var n = (name ?? "").Trim();
        if (n.Length is 0 or > 64 || n.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@')))
            throw new ArgumentException(L.T("User names: 1–64 letters, digits and . _ - @"));
        if (FindByName(n) is { } other && other.Id != self) throw new ArgumentException(L.T("An account named \"{0}\" already exists.", n));
        return n;
    }

    public static void CheckPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new ArgumentException(L.T("The password needs at least {0} characters.", MinPasswordLength));
        if (password.Length > 1024) throw new ArgumentException(L.T("The password is too long."));
    }

    private static AccountPermissions Normalize(AccountPermissions p)
    {
        var roots = p.Roots?.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct().ToList();
        return p with { Roots = roots is { Count: > 0 } ? roots : null };
    }

    private Account? FindByName(string name) =>
        _accounts.FirstOrDefault(a => string.Equals(a.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    private int ActiveAdmins() => _accounts.Count(a => a.IsAdmin && !a.Disabled);

    private void Replace(Account a)
    {
        var i = _accounts.FindIndex(x => x.Id == a.Id);
        if (i >= 0) _accounts[i] = a;
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private void Refresh()
    {
        try
        {
            if (File.Exists(AccountsPath) && File.GetLastWriteTimeUtc(AccountsPath) != _accountsStamp) Load();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { /* keep what is in memory */ }
    }

    private void Load()
    {
        lock (_lock)
        {
            _sessions.Clear();
            if (File.Exists(AccountsPath))
            {
                _accountsStamp = File.GetLastWriteTimeUtc(AccountsPath);
                // A broken file must not open the server to everybody (an empty list means "first start"): stop instead.
                var text = File.ReadAllText(AccountsPath);
                var file = JsonSerializer.Deserialize<AccountsFile>(text, SettingsStore.Json)
                           ?? throw new InvalidDataException(AccountsPath);
                _accounts = file.Accounts;
            }
            try
            {
                if (File.Exists(SessionsPath)
                    && JsonSerializer.Deserialize<SessionsFile>(File.ReadAllText(SessionsPath), SettingsStore.Json) is { } sf)
                {
                    var now = _now();
                    foreach (var s in sf.Sessions.Where(s => now - s.LastSeen <= (s.Persistent ? RememberedSessionIdle : SessionIdle)))
                        _sessions[s.TokenHash] = s;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* everybody signs in again */ }
        }
    }

    private void SaveAccounts()
    {
        WritePrivate(AccountsPath, JsonSerializer.Serialize(new AccountsFile(1, _accounts), SettingsStore.Json));
        _accountsStamp = File.GetLastWriteTimeUtc(AccountsPath);
    }

    private void SaveSessions(bool force)
    {
        var now = _now();
        if (!force && now - _sessionsSaved < TimeSpan.FromMinutes(10)) return;
        _sessionsSaved = now;
        try { WritePrivate(SessionsPath, JsonSerializer.Serialize(new SessionsFile([.. _sessions.Values]), SettingsStore.Json)); }
        catch (IOException) { /* sessions survive in memory */ }
    }

    /// <summary>Atomic write of a file only the service user may read.</summary>
    private static void WritePrivate(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        File.Move(tmp, path, overwrite: true);
    }
}
