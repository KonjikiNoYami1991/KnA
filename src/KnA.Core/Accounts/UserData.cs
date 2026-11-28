using System.Collections.Concurrent;
using System.Text.Json;
using KnA.Core.Encoding;
using KnA.Core.Settings;

namespace KnA.Core.Accounts;

/// <summary>What a web account keeps for itself; null fields fall back to the server's (the administrators') values.</summary>
public sealed record UserSettings
{
    /// <summary>The defaults of the quick form.</summary>
    public JobSettings? Defaults { get; init; }
    public string? Theme { get; init; }
}

/// <summary>
/// Each web account's own presets and settings: users/&lt;id&gt;/presets.json and settings.json in the data folder.
/// An account sees the built-in presets, the shared ones (the administrators': presets.json of the data folder, also
/// used by the desktop app, watch folders and the command line) and its own; an own preset with the name of a shared
/// one takes its place for that account. Administrators save into the shared presets and settings and may see,
/// share and delete every account's.
/// </summary>
public sealed class UserDataStore(string dataDirectory, PresetStore shared)
{
    private readonly ConcurrentDictionary<Guid, PresetStore> _presets = new();
    private readonly object _lock = new();

    public PresetStore Shared => shared;
    private string UsersFolder => Path.Combine(dataDirectory, "users");
    public string FolderOf(Guid id) => Path.Combine(UsersFolder, id.ToString("N"));

    /// <summary>The account's own presets (not the shared ones).</summary>
    public PresetStore PresetsOf(Guid id) => _presets.GetOrAdd(id, x => new PresetStore(FolderOf(x)));

    /// <summary>
    /// Every preset the account sees, each marked <see cref="Preset.Scope"/> "own" or "shared"; with no account
    /// (<paramref name="ownPresets"/> false: administrators, accounts switched off) the built-in and shared ones.
    /// </summary>
    public IReadOnlyList<Preset> Visible(Guid id, bool ownPresets)
    {
        var own = ownPresets ? PresetsOf(id).User : [];
        var names = own.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [
            .. PresetStore.BuiltIns,
            .. shared.User.Where(p => !names.Contains(p.Name)).Select(p => p with { Scope = "shared" }),
            .. own.Select(p => p with { Scope = "own" }),
        ];
    }

    /// <summary>A preset by name as the account sees it: its own first, then the shared and built-in ones.</summary>
    public Preset? Get(Guid id, bool ownPresets, string name) =>
        (ownPresets ? PresetsOf(id).User.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : null)
        ?? shared.Get(name);

    /// <summary>Every account's own presets (the administrators' view), by account id.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<Preset>> AllOwnPresets()
    {
        var result = new Dictionary<Guid, IReadOnlyList<Preset>>();
        if (!Directory.Exists(UsersFolder)) return result;
        foreach (var dir in Directory.EnumerateDirectories(UsersFolder))
            if (Guid.TryParseExact(Path.GetFileName(dir), "N", out var id) && PresetsOf(id).User is { Count: > 0 } list)
                result[id] = list;
        return result;
    }

    // ------------------------------------------------------------------ settings

    public UserSettings SettingsOf(Guid id)
    {
        var path = Path.Combine(FolderOf(id), "settings.json");
        lock (_lock)
        {
            try
            {
                if (File.Exists(path)) return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), SettingsStore.Json) ?? new();
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { }
            return new();
        }
    }

    public void SaveSettings(Guid id, UserSettings settings)
    {
        var dir = FolderOf(id);
        var path = Path.Combine(dir, "settings.json");
        lock (_lock)
        {
            Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SettingsStore.Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>The account was deleted: its presets and settings go with it.</summary>
    public void Delete(Guid id)
    {
        _presets.TryRemove(id, out _);
        lock (_lock)
            try { if (Directory.Exists(FolderOf(id))) Directory.Delete(FolderOf(id), recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }
}
