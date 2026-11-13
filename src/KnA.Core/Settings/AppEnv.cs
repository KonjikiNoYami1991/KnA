namespace KnA.Core.Settings;

/// <summary>
/// Environment variables (KNA_* prefix).
/// </summary>
public static class AppEnv
{
    /// <summary>Value of KNA_<paramref name="name"/>; null when unset or empty.</summary>
    public static string? Get(string name)
    {
        var v = Environment.GetEnvironmentVariable("KNA_" + name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static bool Flag(string name) => Get(name)?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public static int Int(string name, int fallback) =>
        int.TryParse(Get(name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Default data folder (AppData/KnA, ~/.config/KnA…).</summary>
    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "KnA");
}
