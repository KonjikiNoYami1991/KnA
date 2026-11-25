using System.Collections.Concurrent;

namespace KnA.Core.Accounts;

/// <summary>
/// Brute-force throttling of sign-ins, per client address and per user name: the first
/// <see cref="FreeAttempts"/> failures cost nothing, then each further one locks the key for twice as long
/// (2 s, 4 s, 8 s… up to <see cref="MaxLock"/>). A quiet <see cref="Forget"/> period, or a successful sign-in,
/// clears the count. Checked before the (slow) password hash runs, so a flood of guesses costs no CPU either.
/// </summary>
public sealed class LoginThrottle(Func<DateTimeOffset>? clock = null)
{
    public const int FreeAttempts = 5;
    public static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Forget = TimeSpan.FromMinutes(30);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private sealed record Entry(int Failures, DateTimeOffset Last, DateTimeOffset LockedUntil);

    public static string IpKey(string? address) => "ip:" + (address ?? "?");
    public static string UserKey(string? name) => "user:" + (name ?? "").Trim().ToLowerInvariant();

    /// <summary>How long the caller has to wait before trying again (zero = may try now).</summary>
    public TimeSpan RetryAfter(params string[] keys)
    {
        var now = _now();
        var wait = TimeSpan.Zero;
        foreach (var k in keys)
            if (_entries.TryGetValue(k, out var e) && e.LockedUntil > now && e.LockedUntil - now > wait) wait = e.LockedUntil - now;
        return wait;
    }

    public void Failed(params string[] keys)
    {
        var now = _now();
        foreach (var k in keys)
            _entries.AddOrUpdate(k, _ => Next(0, now), (_, e) => Next(now - e.Last > Forget ? 0 : e.Failures, now));
        if (_entries.Count > 50_000) Prune(now);
    }

    public void Succeeded(params string[] keys)
    {
        foreach (var k in keys) _entries.TryRemove(k, out _);
    }

    private static Entry Next(int failures, DateTimeOffset now)
    {
        failures++;
        var over = failures - FreeAttempts;
        var lockFor = over <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(MaxLock.TotalSeconds, Math.Pow(2, Math.Min(over, 20))));
        return new Entry(failures, now, now + lockFor);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (k, e) in _entries)
            if (now - e.Last > Forget && e.LockedUntil < now) _entries.TryRemove(k, out _);
    }
}
