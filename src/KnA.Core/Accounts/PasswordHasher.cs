using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KnA.Core.Accounts;

/// <summary>
/// Salted, slow password hashes: PBKDF2-HMAC-SHA256 with a random 16-byte salt, stored as
/// "pbkdf2-sha256$iterations$salt$hash" (base64), so the cost can be raised later without breaking old hashes
/// (<see cref="NeedsRehash"/>).
/// </summary>
public static class PasswordHasher
{
    /// <summary>OWASP's recommendation for PBKDF2-HMAC-SHA256 (Password Storage Cheat Sheet).</summary>
    public const int DefaultIterations = 600_000;
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16, HashBytes = 32;

    /// <summary>Iterations of new hashes (lowered by the tests, which hash a lot).</summary>
    internal static int Iterations { get; set; } = DefaultIterations;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (!TryParse(stored, out var iterations, out var salt, out var expected)) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Same work as a real check, for unknown user names (no timing difference to probe them with).</summary>
    public static void VerifyDummy(string password) => Verify(password, DummyHash.Value);
    private static readonly Lazy<string> DummyHash = new(() => Hash(Guid.NewGuid().ToString()));

    /// <summary>A hash made with fewer iterations than today's: re-hashed at the next successful login.</summary>
    public static bool NeedsRehash(string? stored) => !TryParse(stored, out var it, out _, out _) || it < Iterations;

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0; salt = hash = [];
        var parts = (stored ?? "").Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) || iterations < 1) return false;
        try { salt = Convert.FromBase64String(parts[2]); hash = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return false; }
        return salt.Length > 0 && hash.Length > 0;
    }
}
