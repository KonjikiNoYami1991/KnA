namespace KnA.Core.Settings;

/// <summary>
/// Environment variables. The program was called TnA before 3.0: every KNA_* variable is also read with
/// the old TNA_* prefix, so existing docker-compose files, systemd units and scripts keep working.
/// </summary>
public static class AppEnv
{
    /// <summary>Value of KNA_<paramref name="name"/> (or legacy TNA_<paramref name="name"/>); null when unset or empty.</summary>
    public static string? Get(string name)
    {
        var v = Environment.GetEnvironmentVariable("KNA_" + name);
        if (string.IsNullOrEmpty(v)) v = Environment.GetEnvironmentVariable("TNA_" + name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static bool Flag(string name) => Get(name)?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public static int Int(string name, int fallback) =>
        int.TryParse(Get(name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>
    /// Default data folder (AppData/KnA, ~/.config/KnA…). A data folder left by TnA 2.x is renamed once,
    /// so settings, presets, the queue and downloaded components survive the rename.
    /// </summary>
    public static string DefaultDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        var current = Path.Combine(root, "KnA");
        var legacy = Path.Combine(root, "TnA");
        try
        {
            if (!Directory.Exists(current) && Directory.Exists(legacy)) Directory.Move(legacy, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or not movable: keep using the old folder rather than starting from scratch.
            return legacy;
        }
        return current;
    }
}
