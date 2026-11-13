using System.Collections.Concurrent;
using System.Text.Json;
using KnA.Core.Localization;
using KnA.Core.Settings;

namespace KnA.Server;

public sealed record UploadInfo(string Id, string Name, long Size, string Folder, long Offset, DateTimeOffset Created)
{
    public string PartPath => Path.Combine(Folder, $".{Name}.kna-upload-{Id}");
}

/// <summary>
/// Chunked, resumable uploads straight into the destination folder (no copy across disks at the end):
/// POST creates "<folder>/.<name>.kna-upload-<id>", PUT appends chunks at the expected offset
/// (a lost chunk is simply sent again from the offset the server reports), POST complete renames the
/// file to its final, non-colliding name. Works behind reverse proxies with request size limits.
/// </summary>
public sealed class UploadService
{
    private readonly ConcurrentDictionary<string, UploadInfo> _uploads = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly string _stateDir;

    /// <summary>Largest chunk accepted (the browser sends 16 MB).</summary>
    public const int MaxChunk = 64 << 20;
    public long MaxFileBytes { get; } = AppEnv.Int("MAX_UPLOAD_GB", 0) is > 0 and var gb ? gb * (1L << 30) : long.MaxValue;
    /// <summary>Default destination: $KNA_UPLOAD_DIR, else "uploads" in the data folder.</summary>
    public string DefaultFolder { get; }

    public UploadService(SettingsStore store)
    {
        _stateDir = Path.Combine(store.DataDirectory, "uploads-state");
        Directory.CreateDirectory(_stateDir);
        DefaultFolder = AppEnv.Get("UPLOAD_DIR") ?? Path.Combine(store.DataDirectory, "uploads");
        Directory.CreateDirectory(DefaultFolder);
        foreach (var f in Directory.EnumerateFiles(_stateDir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<UploadInfo>(File.ReadAllText(f)) is { } u && File.Exists(u.PartPath)
                    && DateTimeOffset.Now - u.Created < TimeSpan.FromDays(7))
                    _uploads[u.Id] = u with { Offset = new FileInfo(u.PartPath).Length };
                else
                {
                    File.Delete(f);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
    }

    /// <summary>File name without any folder part or characters that are invalid on Windows/Linux.</summary>
    public static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*'])) name = name.Replace(c, '_');
        name = name.Trim().TrimStart('.');
        if (name.Length == 0) name = "upload";
        return name.Length > 200 ? name[..200] : name;
    }

    public UploadInfo Create(string name, long size, string folder)
    {
        if (size < 0 || size > MaxFileBytes) throw new InvalidOperationException(L.T("The file is too big for this server."));
        Directory.CreateDirectory(folder);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            if (drive.IsReady && drive.AvailableFreeSpace < size + (256L << 20))
                throw new InvalidOperationException(L.T("Not enough free space in {0} ({1} MB left).", folder, drive.AvailableFreeSpace >> 20));
        }
        catch (ArgumentException) { }
        // Same file again (same name, size, folder): resume the existing upload.
        var safe = SafeName(name);
        if (_uploads.Values.FirstOrDefault(u => u.Name == safe && u.Size == size && u.Folder == folder) is { } existing)
            return existing with { Offset = File.Exists(existing.PartPath) ? new FileInfo(existing.PartPath).Length : 0 };
        var info = new UploadInfo(Guid.NewGuid().ToString("N")[..16], safe, size, folder, 0, DateTimeOffset.Now);
        File.WriteAllBytes(info.PartPath, []);
        _uploads[info.Id] = info;
        Persist(info);
        return info;
    }

    public UploadInfo? Get(string id) =>
        _uploads.TryGetValue(id, out var u) ? u with { Offset = File.Exists(u.PartPath) ? new FileInfo(u.PartPath).Length : 0 } : null;

    /// <summary>Appends a chunk. Returns the new offset, or the current one (mismatch) so the client can resume from there.</summary>
    public async Task<(bool Ok, long Offset)> AppendAsync(string id, long offset, Stream body, CancellationToken ct)
    {
        var u = _uploads.GetValueOrDefault(id) ?? throw new KeyNotFoundException();
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            await using var fs = new FileStream(u.PartPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            if (offset != fs.Length) return (false, fs.Length);
            fs.Seek(0, SeekOrigin.End);
            var buffer = new byte[1 << 20];
            long written = 0;
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                written += n;
                if (written > MaxChunk || fs.Length + n > u.Size)
                {
                    fs.SetLength(offset);
                    throw new InvalidOperationException(L.T("The upload is larger than announced."));
                }
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            return (true, fs.Length);
        }
        finally { gate.Release(); }
    }

    /// <summary>Renames the finished upload to "name.ext", "name (2).ext"… and returns the final path.</summary>
    public string Complete(string id)
    {
        var u = _uploads.GetValueOrDefault(id) ?? throw new KeyNotFoundException();
        long len = new FileInfo(u.PartPath).Length;
        if (len != u.Size) throw new InvalidOperationException(L.T("Upload incomplete: {0} of {1} bytes.", len, u.Size));
        var target = UniquePath(Path.Combine(u.Folder, u.Name));
        File.Move(u.PartPath, target);
        Forget(id);
        return target;
    }

    public void Cancel(string id)
    {
        if (_uploads.TryGetValue(id, out var u))
            try { File.Delete(u.PartPath); } catch (IOException) { }
        Forget(id);
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    private void Forget(string id)
    {
        _uploads.TryRemove(id, out _);
        _locks.TryRemove(id, out _);
        try { File.Delete(Path.Combine(_stateDir, id + ".json")); } catch (IOException) { }
    }

    private void Persist(UploadInfo u)
    {
        try { File.WriteAllText(Path.Combine(_stateDir, u.Id + ".json"), JsonSerializer.Serialize(u)); } catch (IOException) { }
    }
}
