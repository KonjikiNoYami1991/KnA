using System.Collections.Concurrent;
using System.Text.Json;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media.Discs;
using KnA.Core.Settings;

namespace KnA.Core.Queue;

public enum WatchAfterSuccess { Keep, MoveToDoneFolder, Delete }

/// <summary>A folder whose new files are queued automatically with a preset.</summary>
public sealed record WatchFolder
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Path { get; init; } = "";
    public bool Enabled { get; init; } = true;
    /// <summary>Preset applied to the new files (resolved when they are queued); null = <see cref="Settings"/> or the defaults.</summary>
    public string? Preset { get; init; }
    public JobSettings? Settings { get; init; }
    /// <summary>Where the results go; null = global output folder / next to the source.</summary>
    public string? OutputFolder { get; init; }
    public bool Recursive { get; init; } = true;
    /// <summary>Start the queue when files are added.</summary>
    public bool AutoStart { get; init; } = true;
    /// <summary>Queue the files already there when the folder is added (otherwise only new ones).</summary>
    public bool ImportExisting { get; init; }
    public WatchAfterSuccess AfterSuccess { get; init; } = WatchAfterSuccess.Keep;
    /// <summary>Seconds between two scans.</summary>
    public int IntervalSeconds { get; init; } = 30;
    /// <summary>A file must keep the same size and date for this long (still being copied otherwise).</summary>
    public int SettleSeconds { get; init; } = 20;
}

public sealed record WatchStatus(string Id, DateTimeOffset? LastScan, int Queued, int Waiting, string? Error);

/// <summary>
/// Polls the watch folders (polling works on NAS shares and Docker bind mounts, where change notifications
/// often never arrive). A file is queued once its size and date have been stable for the settle time; the files
/// already seen are remembered in watch-state.json so a restart doesn't queue them again.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    private readonly QueueService _queue;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Size, DateTime Time, DateTime FirstSeen)> _pending = [];
    private readonly ConcurrentDictionary<Guid, string> _jobOrigin = new();
    private readonly Dictionary<string, WatchStatus> _status = [];
    private readonly Dictionary<string, DateTimeOffset> _nextScan = [];
    private HashSet<string> _seen;
    private int _busy;

    public event Action<WatchStatus>? StatusChanged;
    public event Action<IReadOnlyList<QueueJob>>? JobsQueued;

    private string StatePath => System.IO.Path.Combine(_queue.Store.DataDirectory, "watch-state.json");

    public WatchFolderService(QueueService queue)
    {
        _queue = queue;
        _seen = LoadSeen();
        _queue.JobChanged += OnJobChanged;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public IReadOnlyList<WatchFolder> Folders => _queue.Settings.WatchFolders;

    public IReadOnlyList<WatchStatus> Status
    {
        get { lock (_lock) return Folders.Select(f => _status.GetValueOrDefault(f.Id) ?? new WatchStatus(f.Id, null, 0, 0, null)).ToList(); }
    }

    /// <summary>Replaces the list (saved in the settings). New folders without "import existing" start from what is there now.</summary>
    public void Save(IReadOnlyList<WatchFolder> folders)
    {
        var old = Folders.Select(f => f.Id).ToHashSet();
        foreach (var f in folders.Where(f => !old.Contains(f.Id) && !f.ImportExisting && Directory.Exists(f.Path)))
            foreach (var file in Candidates(f)) MarkSeen(file);
        _queue.Settings.WatchFolders = [.. folders];
        _queue.SaveSettings();
        lock (_lock) foreach (var f in folders) _nextScan.Remove(f.Id);
        SaveSeen();
    }

    /// <summary>Scans one folder now (ignoring the interval).</summary>
    public void ScanNow(string id)
    {
        lock (_lock) _nextScan.Remove(id);
        Tick();
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            foreach (var f in Folders.Where(f => f.Enabled && f.Path.Length > 0))
            {
                lock (_lock)
                {
                    if (_nextScan.TryGetValue(f.Id, out var when) && when > DateTimeOffset.Now) continue;
                    _nextScan[f.Id] = DateTimeOffset.Now.AddSeconds(Math.Clamp(f.IntervalSeconds, 5, 86400));
                }
                Scan(f);
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Scan(WatchFolder f)
    {
        string? error = null;
        int waiting = 0;
        var ready = new List<string>();
        try
        {
            if (!Directory.Exists(f.Path)) throw new DirectoryNotFoundException(L.T("Folder not found: {0}", f.Path));
            var now = DateTime.UtcNow;
            foreach (var file in Candidates(f))
            {
                if (_seen.Contains(Key(file))) continue;
                var (size, time) = Snapshot(file);
                lock (_lock)
                {
                    if (!_pending.TryGetValue(file, out var p) || p.Size != size || p.Time != time)
                    {
                        _pending[file] = (size, time, now);
                        waiting++;
                        continue;
                    }
                    if ((now - p.FirstSeen).TotalSeconds < Math.Max(0, f.SettleSeconds) || !CanOpen(file))
                    {
                        waiting++;
                        continue;
                    }
                    _pending.Remove(file);
                }
                ready.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }

        var added = new List<QueueJob>();
        if (ready.Count > 0)
        {
            var settings = ResolveSettings(f);
            var edit = f.OutputFolder is { Length: > 0 } of ? new JobEdit { OutputFolder = of } : null;
            foreach (var file in ready)
            {
                MarkSeen(file);
                var jobs = _queue.AddPaths([file], settings, null, edit);
                foreach (var j in jobs) _jobOrigin[j.Id] = f.Id;
                added.AddRange(jobs);
            }
            SaveSeen();
            if (added.Count > 0)
            {
                JobsQueued?.Invoke(added);
                if (f.AutoStart && !_queue.IsRunning)
                    try { _queue.Start(); } catch (InvalidOperationException ex) { error = ex.Message; }
            }
        }

        WatchStatus status;
        lock (_lock)
        {
            var prev = _status.GetValueOrDefault(f.Id);
            status = new WatchStatus(f.Id, DateTimeOffset.Now, (prev?.Queued ?? 0) + added.Count, waiting, error);
            _status[f.Id] = status;
        }
        StatusChanged?.Invoke(status);
    }

    /// <summary>The folder's preset (by name, at queue time) → its own settings → the defaults.</summary>
    public JobSettings ResolveSettings(WatchFolder f)
    {
        if (f.Preset is { Length: > 0 } name && _queue.Presets.All.FirstOrDefault(p => p.Name == name) is { } preset)
            return preset.Settings with { PresetName = preset.Name };
        return f.Settings ?? _queue.Settings.Defaults;
    }

    /// <summary>Video files (outputs of KnA excluded), .iso images and disc folders.</summary>
    private IEnumerable<string> Candidates(WatchFolder f)
    {
        var exts = _queue.Settings.FolderExtensions.Select(e => e.ToLowerInvariant()).ToHashSet();
        var opt = new EnumerationOptions { RecurseSubdirectories = f.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var doneDir = System.IO.Path.Combine(f.Path, DoneFolderName);
        foreach (var file in Directory.EnumerateFiles(f.Path, "*", opt))
        {
            var name = System.IO.Path.GetFileName(file);
            if (name.StartsWith('.') || name.Contains(".kna-upload", StringComparison.Ordinal)) continue;
            if (file.StartsWith(doneDir, StringComparison.Ordinal) || DiscSource.IsInsideDiscStructure(file)) continue;
            if (f.OutputFolder is { Length: > 0 } of && file.StartsWith(of, StringComparison.Ordinal)) continue;
            if (!exts.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()) || QueueService.IsOwnOutput(file)) continue;
            yield return file;
        }
        foreach (var d in DiscSource.FindDiscs(f.Path, f.Recursive ? 8 : 1).Where(d => !d.IsIso))
            yield return d.Root;
    }

    public const string DoneFolderName = "_done";

    private static (long, DateTime) Snapshot(string path)
    {
        if (Directory.Exists(path))
        {
            var files = new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            return (files.Sum(x => x.Length), files.Count == 0 ? DateTime.MinValue : files.Max(x => x.LastWriteTimeUtc));
        }
        var fi = new FileInfo(path);
        return (fi.Length, fi.LastWriteTimeUtc);
    }

    /// <summary>On Windows a file still being copied cannot be opened for reading by others.</summary>
    private static bool CanOpen(string path)
    {
        if (Directory.Exists(path)) return true;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void OnJobChanged(QueueJob job)
    {
        if (job.Status is not (JobStatus.Done or JobStatus.Warning) || !_jobOrigin.TryRemove(job.Id, out var id)) return;
        if (Folders.FirstOrDefault(f => f.Id == id) is not { } f || f.AfterSuccess == WatchAfterSuccess.Keep) return;
        var source = DiscSource.ParseTitlePath(job.SourcePath)?.Disc.Root ?? job.SourcePath;
        // Several titles of one disc: act only when none of them is still waiting.
        if (_jobOrigin.Keys.Select(_queue.Find).Any(j => j is not null && (DiscSource.ParseTitlePath(j.SourcePath)?.Disc.Root ?? j.SourcePath) == source)) return;
        try
        {
            if (f.AfterSuccess == WatchAfterSuccess.Delete)
            {
                if (Directory.Exists(source)) Directory.Delete(source, true); else File.Delete(source);
                job.AppendLog("Watch folder: source deleted.");
            }
            else
            {
                var rel = System.IO.Path.GetRelativePath(f.Path, source);
                var target = System.IO.Path.Combine(f.Path, DoneFolderName, rel);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target, overwrite: false);
                job.AppendLog("Watch folder: source moved to " + target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            job.AppendLog("Watch folder: " + ex.Message);
        }
    }

    private static string Key(string path)
    {
        var (size, time) = Snapshot(path);
        return $"{path}|{size}|{time.Ticks}";
    }

    private void MarkSeen(string file)
    {
        try { lock (_lock) _seen.Add(Key(file)); } catch (IOException) { }
    }

    private HashSet<string> LoadSeen()
    {
        try
        {
            if (File.Exists(StatePath)) return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StatePath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
        return [];
    }

    private void SaveSeen()
    {
        try
        {
            string json;
            lock (_lock)
            {
                // Forget files that no longer exist so the state does not grow forever.
                _seen = _seen.Where(k => k.Split('|')[0] is var p && (File.Exists(p) || Directory.Exists(p))).ToHashSet();
                json = JsonSerializer.Serialize(_seen);
            }
            File.WriteAllText(StatePath, json);
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _queue.JobChanged -= OnJobChanged;
    }
}
