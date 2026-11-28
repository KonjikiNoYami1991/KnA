using System.Security.Cryptography;
using System.Text.Json;
using KnA.Core.Localization;
using KnA.Core.Settings;

namespace KnA.Core.Remote;

/// <summary>A folder of this computer that is the same as a folder of the server (a shared NAS folder).</summary>
public sealed record PathMapping(string Local, string Server);

/// <summary>
/// Files of this computer the KnA server can already read (the same NAS share, mounted on both): found by themselves —
/// the end of the path, the size and a sampled checksum must match, never a path to type — and used in place instead
/// of being uploaded. Each match teaches a mapping between the two folders (kept in remote-paths.json), so the next
/// files of the share are recognised at once; results written in the share are already "home".
/// </summary>
public sealed class ServerPaths
{
    private sealed record Located(string? Path);

    private readonly KnaClient _client;
    private readonly string _statePath;
    private readonly string _server;
    private readonly object _lock = new();
    private Dictionary<string, List<PathMapping>> _all = [];

    public ServerPaths(KnaClient client, string dataDirectory)
    {
        _client = client;
        _server = client.BaseUri.ToString();
        _statePath = Path.Combine(dataDirectory, "remote-paths.json");
        try
        {
            if (File.Exists(_statePath))
                _all = JsonSerializer.Deserialize<Dictionary<string, List<PathMapping>>>(File.ReadAllText(_statePath), SettingsStore.Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { }
    }

    public IReadOnlyList<PathMapping> Mappings { get { lock (_lock) return _all.GetValueOrDefault(_server) is { } l ? [.. l] : []; } }

    /// <summary>
    /// The server's path of a file of this computer when the server can read the very same file; null otherwise (it is
    /// uploaded then). Network errors count as "not found": the upload is the safe way.
    /// </summary>
    public async Task<string?> LocateAsync(string localPath, CancellationToken ct = default)
    {
        if (!File.Exists(localPath)) return null;
        var size = new FileInfo(localPath).Length;
        string hash;
        try { hash = await FileFingerprint.SampledAsync(localPath, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var candidates = Mappings.Select(m => Map(localPath, m)).OfType<string>().Distinct().ToList();
        Located found;
        try { found = await _client.SendJsonAsync<Located>(HttpMethod.Post, "api/files/locate", new { segments = Segments(localPath), size, hash, candidates }, ct); }
        catch (RemoteException) { return null; }
        if (found?.Path is not { } serverPath) return null;
        if (Learn(localPath, serverPath) is { } m) Remember(m);
        return serverPath;
    }

    /// <summary>The path this computer sees for a file of the server (a result written in the share), if it is mapped.</summary>
    public string? ToLocal(string serverPath) => Mappings.Select(m => Unmap(serverPath, m)).OfType<string>().FirstOrDefault();

    private void Remember(PathMapping m)
    {
        lock (_lock)
        {
            var list = _all.TryGetValue(_server, out var l) ? l : _all[_server] = [];
            if (list.Any(x => Same(x.Local, m.Local) && x.Server == m.Server)) return;
            list.RemoveAll(x => Same(x.Local, m.Local));   // the share moved on the server: the new place wins
            list.Insert(0, m);
            try
            {
                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_all, SettingsStore.Json));
                File.Move(tmp, _statePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ the path arithmetic (pure, tested)

    public static List<string> Segments(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).ToList();

    private static char SeparatorOf(string path) => path.Contains('\\') && !path.Contains('/') ? '\\' : '/';
    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a match teaches: the longest common end of the two paths (folders and name) is the shared part, what is
    /// before it is the same folder seen from each side ("Z:\" = "/mnt/nas/video"). Null when only the name matches.
    /// </summary>
    public static PathMapping? Learn(string localPath, string serverPath)
    {
        var l = Segments(localPath);
        var s = Segments(serverPath);
        int common = 0;
        while (common < l.Count && common < s.Count && string.Equals(l[^(common + 1)], s[^(common + 1)], StringComparison.OrdinalIgnoreCase)) common++;
        if (common < 2) return null;   // only the file name: not enough to map a folder
        return new PathMapping(Prefix(localPath, l.Count - common), Prefix(serverPath, s.Count - common));
    }

    /// <summary>The first <paramref name="count"/> segments of <paramref name="path"/>, written as that system writes them.</summary>
    private static string Prefix(string path, int count)
    {
        var sep = SeparatorOf(path);
        var segs = Segments(path).Take(count).ToList();
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\" + string.Join('\\', segs);          // \\nas\share
        var body = string.Join(sep, segs);
        if (segs.Count > 0 && segs[0].EndsWith(':')) return body.Length == segs[0].Length ? body + sep : body; // Z:\ , Z:\video
        return (path.StartsWith('/') ? "/" : "") + body;
    }

    /// <summary>The server's path of a local file under a mapped folder (null when it is not under it).</summary>
    public static string? Map(string localPath, PathMapping m)
    {
        var rest = Relative(localPath, m.Local);
        if (rest is null) return null;
        var sep = SeparatorOf(m.Server);
        return m.Server.TrimEnd('/', '\\') + sep + string.Join(sep, rest);
    }

    /// <summary>The local path of a server file under a mapped folder.</summary>
    public static string? Unmap(string serverPath, PathMapping m)
    {
        var rest = Relative(serverPath, m.Server);
        if (rest is null) return null;
        var local = m.Local.TrimEnd('/', '\\');
        return local + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, rest);
    }

    private static List<string>? Relative(string path, string folder)
    {
        var p = Segments(path);
        var f = Segments(folder);
        if (f.Count >= p.Count) return null;
        for (int i = 0; i < f.Count; i++)
            if (!string.Equals(p[i], f[i], StringComparison.OrdinalIgnoreCase)) return null;
        return p.Skip(f.Count).ToList();
    }
}

/// <summary>
/// A quick fingerprint of a big file read over the network: its size and SHA-256 of three 1 MB samples (start, middle,
/// end). The same file on both sides gives the same value; a different file of the same size practically never does.
/// </summary>
public static class FileFingerprint
{
    private const int Sample = 1 << 20;

    public static async Task<string> SampledAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Sample, useAsync: true);
        long size = fs.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(BitConverter.GetBytes(size));
        var buffer = new byte[Sample];
        int n;
        if (size <= 3L * Sample)
        {
            // A small file is hashed whole.
            while ((n = await fs.ReadAsync(buffer, ct)) > 0) sha.AppendData(buffer, 0, n);
        }
        else
            foreach (var at in new[] { 0L, size / 2 - Sample / 2, size - Sample })
            {
                fs.Position = at;
                n = await fs.ReadAtLeastAsync(buffer, Sample, throwOnEndOfStream: false, ct);
                sha.AppendData(buffer, 0, n);
            }
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}
