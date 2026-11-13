namespace KnA.Core.Settings;

/// <summary>
/// Environment variables (KNA_* prefix).
/// </summary>
public static class AppEnv
{
    /// <summary>Name of the marker file that turns a copy of KnA into a portable one.</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>Value of KNA_<paramref name="name"/>; null when unset or empty.</summary>
    public static string? Get(string name)
    {
        var v = Environment.GetEnvironmentVariable("KNA_" + name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static bool Flag(string name) => Get(name)?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public static int Int(string name, int fallback) =>
        int.TryParse(Get(name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>
    /// Default data folder: "data" next to the program when it is portable (see <see cref="PortableDataDirectory"/>),
    /// otherwise AppData/KnA, ~/.config/KnA…
    /// </summary>
    public static string DefaultDataDirectory() =>
        PortableDataDirectory(AppContext.BaseDirectory, Flag("PORTABLE"))
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "KnA");

    /// <summary>
    /// "data" inside <paramref name="programDirectory"/> when that folder holds <see cref="PortableMarker"/> (or
    /// <paramref name="forced"/> is set) and can be written to; null otherwise, e.g. a portable zip unpacked into
    /// Program Files, which then falls back to the per-user folder instead of failing.
    /// </summary>
    public static string? PortableDataDirectory(string programDirectory, bool forced = false)
    {
        if (!forced && !File.Exists(Path.Combine(programDirectory, PortableMarker))) return null;
        var data = Path.Combine(programDirectory, "data");
        try
        {
            Directory.CreateDirectory(data);
            var probe = Path.Combine(data, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
