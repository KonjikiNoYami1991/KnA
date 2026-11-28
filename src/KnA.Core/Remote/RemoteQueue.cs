using System.Text.Json;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Processing;
using KnA.Core.Queue;
using KnA.Core.Settings;

namespace KnA.Core.Remote;

/// <summary>
/// A KnA server's queue as the desktop app shows it, the same way as this computer's (<see cref="IQueueEngine"/>):
/// the server's jobs mirrored as <see cref="QueueJob"/>s and kept live by its events. Local files are uploaded by
/// themselves (resumable, one at a time, shown in the queue while they go); once uploaded the job belongs to the server,
/// so the laptop may be switched off. Finished results of the files sent from here come back by themselves into the
/// chosen output folder (resumable), then the server forgets them — also at the next connection.
/// </summary>
public sealed class RemoteQueue : IQueueEngine, IAsyncDisposable
{
    /// <summary>What this computer remembers of a job it sent (remote-jobs.json in the data folder).</summary>
    private sealed record Tracked(string Server, string LocalSource, string OutputFolder, string? LocalResult = null, bool InPlace = false);
    /// <summary>A local file waiting to be (or being) uploaded.</summary>
    private sealed record Pending(Guid LocalId, string Server, string LocalSource, string OutputFolder, JobSettings Settings, string? UploadId);
    private sealed record StateFile(Dictionary<Guid, Tracked> Jobs, List<Pending> Uploads);

    private readonly KnaClient _client;
    private readonly string _statePath;
    private readonly string _server;
    private readonly object _lock = new();
    private readonly List<QueueJob> _jobs = [];           // server order, then uploads, then results already home
    private readonly Dictionary<Guid, Tracked> _tracked = [];
    private readonly List<Pending> _pending = [];
    private readonly HashSet<Guid> _downloading = [];
    private readonly HashSet<Guid> _home = [];          // results already back: local, finished jobs
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _uploadGate = new(1), _downloadGate = new(1);
    private Task? _listener;
    private Guid? _current;

    public event Action<QueueJob>? JobChanged;
    public event Action<Guid>? JobRemoved;
    public event Action? QueueChanged;
    /// <summary>Something the person should know (a refused operation, a lost connection, a result back home).</summary>
    public event Action<string, bool>? Notice;

    public KnaClient Client => _client;
    public ServerInfo? Server { get; private set; }
    public Tools.FfmpegCapabilities? Capabilities { get; private set; }
    public bool Connected { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    /// <summary>The server is encoding another account's file.</summary>
    public bool BusyWithOthers { get; private set; }
    public QueueJob? CurrentJob { get { lock (_lock) return _current is { } c ? _jobs.FirstOrDefault(j => j.Id == c) : null; } }
    public IReadOnlyList<QueueJob> Jobs { get { lock (_lock) return [.. _jobs]; } }

    /// <summary>Files of this computer the server reads in place (a shared NAS folder): not uploaded.</summary>
    public ServerPaths Paths { get; }

    public RemoteQueue(KnaClient client, string dataDirectory, ServerPaths? paths = null)
    {
        _client = client;
        Paths = paths ?? new ServerPaths(client, dataDirectory);
        _server = client.BaseUri.ToString();
        _statePath = Path.Combine(dataDirectory, "remote-jobs.json");
        Load();
    }

    // ------------------------------------------------------------------ connection

    /// <summary>Checks the server and the account, mirrors the queue and starts listening; resumes uploads and downloads.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Server = await _client.VersionAsync(ct);
        await _client.SignInAsync(ct);
        await SyncAsync(ct);
        Connected = true;
        _listener = Task.Run(ListenLoopAsync);
        lock (_lock)
            foreach (var p in _pending.Where(p => p.Server == _server))
                _jobs.Add(Placeholder(p));
        foreach (var j in Jobs) JobChanged?.Invoke(j);
        QueueChanged?.Invoke();
        _ = Task.Run(UploadLoopAsync);
        _ = Task.Run(CollectFinishedAsync);
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        var st = await _client.StateAsync(ct);
        Capabilities = st.Settings?.Capabilities;
        var seen = st.Jobs.Select(j => j.Id).ToHashSet();
        List<Guid> gone;
        lock (_lock)
        {
            gone = _jobs.Where(IsServerJob).Select(j => j.Id).Where(id => !seen.Contains(id)).ToList();
            _jobs.RemoveAll(j => gone.Contains(j.Id));
        }
        foreach (var id in gone) JobRemoved?.Invoke(id);
        foreach (var dto in st.Jobs) Apply(dto);
        ApplyQueue(st.Queue, st.Jobs.Select(j => j.Id).ToList());
        // Jobs sent from here that the server no longer has (removed in the web page): forgotten.
        lock (_lock)
            foreach (var id in _tracked.Where(t => t.Value.Server == _server && !seen.Contains(t.Key)).Select(t => t.Key).ToList())
                _tracked.Remove(id);
        Save();
    }

    private bool IsServerJob(QueueJob j) => !_home.Contains(j.Id) && !_pending.Any(p => p.LocalId == j.Id);

    private async Task ListenLoopAsync()
    {
        int failures = 0;
        while (!_life.IsCancellationRequested)
        {
            try
            {
                if (failures > 0) await SyncAsync(_life.Token);
                if (!Connected) { Connected = true; Notice?.Invoke(L.T("Connected again to the KnA server."), false); QueueChanged?.Invoke(); }
                failures = 0;
                await _client.ListenAsync(OnEvent, _life.Token);
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is RemoteException or IOException or HttpRequestException or OperationCanceledException or JsonException)
            {
                if (Connected && ++failures >= 2)
                {
                    Connected = false;
                    Notice?.Invoke(L.T("The connection to the KnA server was lost: {0} — retrying by itself.", ex.Message), true);
                    QueueChanged?.Invoke();
                }
                if (failures == 0) failures = 1;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Max(1, failures))), _life.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnEvent(string type, JsonElement payload)
    {
        switch (type)
        {
            case "job":
                Apply(payload.Deserialize<RemoteJob>(SettingsStore.Json)!);
                break;
            case "removed":
                var id = payload.GetProperty("id").GetGuid();
                bool had;
                lock (_lock) had = _jobs.RemoveAll(j => j.Id == id && IsServerJob(j)) > 0;
                if (had) JobRemoved?.Invoke(id);
                break;
            case "queue":
                ApplyQueue(payload.GetProperty("queue").Deserialize<RemoteQueueState>(SettingsStore.Json)!,
                    payload.GetProperty("order").EnumerateArray().Select(x => x.GetGuid()).ToList());
                break;
        }
    }

    private void Apply(RemoteJob dto)
    {
        QueueJob job;
        lock (_lock)
        {
            if (_home.Contains(dto.Id)) return;   // back here already; the server is forgetting it
            job = _jobs.FirstOrDefault(j => j.Id == dto.Id) ?? new QueueJob { Id = dto.Id, SourcePath = dto.SourcePath };
            if (!_jobs.Contains(job)) _jobs.Insert(_jobs.Count(IsServerJob), job);
        }
        Mirror(job, dto);
        JobChanged?.Invoke(job);
        if (dto.Status is JobStatus.Done or JobStatus.Warning && dto.OutputPath is not null && _tracked.ContainsKey(dto.Id))
            _ = Task.Run(() => BringHomeAsync(dto.Id, dto.OutputPath));
    }

    /// <summary>The server's view of a job written into a local mirror.</summary>
    internal static void Mirror(QueueJob job, RemoteJob dto)
    {
        job.Settings = dto.Settings;
        job.Edit = dto.Edit;
        job.Status = dto.Status;
        job.Message = dto.Message;
        job.Stage = dto.Stage;
        job.OutputPath = dto.OutputPath;
        job.ProbeError = dto.ProbeError;
        job.RemoteSummary = dto.MediaSummary ?? (dto.ProbeError is null ? L.T("Analysing…") : null);
        job.StagePercent = dto.Encoding ? null : dto.StagePercent ?? (dto.Indeterminate ? null : dto.Percent > 0 ? dto.Percent : null);
        job.Progress = dto.Encoding
            ? new EncodeProgress(TimeSpan.FromSeconds(dto.PositionSeconds), TimeSpan.FromSeconds(dto.DurationSeconds), dto.Fps ?? 0, dto.Speed ?? 0, dto.Size ?? 0, 0, false)
            : null;
        if (dto.ElapsedSeconds is { } e)
        {
            var now = DateTimeOffset.Now;
            job.StartedAt = now - TimeSpan.FromSeconds(e);
            job.FinishedAt = dto.Status is JobStatus.Running or JobStatus.Paused ? null : now;
        }
        else job.StartedAt = job.FinishedAt = null;
    }

    private void ApplyQueue(RemoteQueueState q, List<Guid> order)
    {
        IsRunning = q.Running;
        IsPaused = q.Paused;
        BusyWithOthers = q.OtherAccount;
        lock (_lock)
        {
            _current = q.CurrentJob;
            var server = _jobs.Where(IsServerJob).ToDictionary(j => j.Id);
            var rest = _jobs.Where(j => !IsServerJob(j)).ToList();
            _jobs.Clear();
            _jobs.AddRange(order.Where(server.ContainsKey).Select(id => server[id]));
            _jobs.AddRange(server.Values.Where(j => !order.Contains(j.Id)));
            _jobs.AddRange(rest);
        }
        QueueChanged?.Invoke();
    }

    // ------------------------------------------------------------------ sending files

    /// <summary>
    /// Local files and folders (videos inside them, by <paramref name="extensions"/>) are uploaded one at a time and
    /// queued on the server with <paramref name="settings"/>; their results come back into <paramref name="outputFolder"/>
    /// (null: next to each source).
    /// </summary>
    public int AddLocalPaths(IEnumerable<string> paths, JobSettings settings, IEnumerable<string> extensions, string? outputFolder)
    {
        var exts = extensions.Select(e => e.ToLowerInvariant()).ToHashSet();
        var files = new List<string>();
        foreach (var raw in paths)
        {
            var path = raw.Trim().Trim('"');
            try
            {
                if (Directory.Exists(path))
                    files.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !QueueService.IsOwnOutput(f)).Order(StringComparer.OrdinalIgnoreCase));
                else if (File.Exists(path) && MediaExtensions.IsVideo(path)) files.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var added = new List<QueueJob>();
        lock (_lock)
            foreach (var f in files)
            {
                var p = new Pending(Guid.NewGuid(), _server, f, outputFolder ?? Path.GetDirectoryName(f) ?? ".", settings, null);
                _pending.Add(p);
                var job = Placeholder(p);
                _jobs.Add(job);
                added.Add(job);
            }
        Save();
        foreach (var j in added) JobChanged?.Invoke(j);
        if (added.Count > 0) { QueueChanged?.Invoke(); _ = Task.Run(UploadLoopAsync); }
        return added.Count;
    }

    private static QueueJob Placeholder(Pending p) => new()
    {
        Id = p.LocalId, SourcePath = p.LocalSource, Settings = p.Settings, Status = JobStatus.Ready,
        Stage = L.T("Waiting to be uploaded to the server"), RemoteSummary = L.T("On this computer: it is sent to the server first."),
    };

    private async Task UploadLoopAsync()
    {
        if (!await _uploadGate.WaitAsync(0)) return;   // one loop at a time
        try
        {
            while (!_life.IsCancellationRequested && Connected)
            {
                Pending? p;
                lock (_lock) p = _pending.FirstOrDefault(x => x.Server == _server && _jobs.Any(j => j.Id == x.LocalId && j.Status != JobStatus.Error));
                if (p is null) return;
                await UploadOneAsync(p);
            }
        }
        finally { _uploadGate.Release(); }
    }

    private async Task UploadOneAsync(Pending p)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == p.LocalId);
        if (job is null) return;
        var size = File.Exists(p.LocalSource) ? new FileInfo(p.LocalSource).Length : 0;
        job.Status = JobStatus.Running;
        job.Stage = L.T("Uploading to the server");
        job.StartedAt = DateTimeOffset.Now;
        JobChanged?.Invoke(job);
        var progress = new Progress<long>(sent =>
        {
            job.StagePercent = size > 0 ? sent * 100.0 / size : null;
            JobChanged?.Invoke(job);
        });
        try
        {
            if (!File.Exists(p.LocalSource)) throw new RemoteException(L.T("Not found: {0}", p.LocalSource));
            // The same file in a folder the server reads too (a NAS share): used where it is, nothing to upload.
            if (p.UploadId is null)
            {
                job.Stage = L.T("Looking for it in the server's folders");
                JobChanged?.Invoke(job);
                if (await Paths.LocateAsync(p.LocalSource, _life.Token) is { } serverPath)
                {
                    var ids = await _client.AddServerPathsAsync([serverPath], p.Settings, _life.Token);
                    Sent(p, job, ids.FirstOrDefault() is var g && g != Guid.Empty ? g : null, inPlace: true);
                    if (ids.Count > 0 && Jobs.All(j => j.Id != ids[0])) await SyncAsync(_life.Token);
                    return;
                }
                job.Stage = L.T("Uploading to the server");
                JobChanged?.Invoke(job);
            }
            var id = await _client.UploadAsync(p.LocalSource, p.Settings, uploadId =>
            {
                lock (_lock) { var i = _pending.IndexOf(p); if (i >= 0) _pending[i] = p = p with { UploadId = uploadId }; }
                Save();
            }, p.UploadId, progress, _life.Token);
            Sent(p, job, id, inPlace: false);
            if (id is null) Notice?.Invoke(L.T("{0} was uploaded but the server did not queue it (not a video it can read?).", Path.GetFileName(p.LocalSource)), true);
            else if (Jobs.All(j => j.Id != id)) await SyncAsync(_life.Token);   // the event may come after
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception ex) when (ex is RemoteException or IOException or HttpRequestException or UnauthorizedAccessException or JsonException)
        {
            job.Status = JobStatus.Error;
            job.Stage = null;
            job.Message = L.T("Upload failed: {0} — Retry sends it again from where it stopped.", ex.Message);
            JobChanged?.Invoke(job);
        }
    }

    /// <summary>The file is the server's job now: the placeholder goes, the job is remembered for its result.</summary>
    private void Sent(Pending p, QueueJob placeholder, Guid? id, bool inPlace)
    {
        lock (_lock)
        {
            _pending.RemoveAll(x => x.LocalId == p.LocalId);
            _jobs.Remove(placeholder);
            if (id is { } jid) _tracked[jid] = new Tracked(_server, p.LocalSource, p.OutputFolder, InPlace: inPlace);
        }
        Save();
        JobRemoved?.Invoke(placeholder.Id);
        QueueChanged?.Invoke();
    }

    // ------------------------------------------------------------------ results back home

    private async Task CollectFinishedAsync()
    {
        foreach (var j in Jobs.Where(j => j.Status is JobStatus.Done or JobStatus.Warning && j.OutputPath is not null && _tracked.ContainsKey(j.Id)))
            await BringHomeAsync(j.Id, j.OutputPath!);
    }

    private async Task BringHomeAsync(Guid id, string serverOutput)
    {
        Tracked? t;
        lock (_lock)
        {
            if (!_tracked.TryGetValue(id, out t) || !_downloading.Add(id)) return;
        }
        await _downloadGate.WaitAsync();
        try
        {
            var job = Jobs.FirstOrDefault(j => j.Id == id);
            if (job is not null)
            {
                job.Stage = L.T("Coming back to this computer");
                JobChanged?.Invoke(job);
            }
            var progress = new Progress<double>(p => { if (job is not null) { job.Stage = L.T("Coming back to this computer: {0:0}%", p); JobChanged?.Invoke(job); } });
            // Downloaded before but not yet forgotten by the server (connection lost in between): not downloaded twice.
            // A file read in place whose result the server wrote into the shared folder, where it should be: nothing to copy.
            var inShare = t.InPlace && Paths.ToLocal(serverOutput) is { } mapped && (File.Exists(mapped) || Directory.Exists(mapped))
                          && IsInside(mapped, t.OutputFolder) ? mapped : null;
            var local = t.LocalResult ?? inShare ?? await _client.DownloadResultAsync(id, serverOutput, t.OutputFolder, progress, _life.Token);
            lock (_lock)
            {
                _tracked[id] = t with { LocalResult = local };
                _home.Add(id);              // kept in the list as a local, finished job
                if (job is not null)
                {
                    job.OutputPath = local;
                    job.Stage = null;
                    job.Message = inShare is not null ? L.T("Written by the server in the shared folder: {0}", local) : L.T("Back on this computer: {0}", local);
                }
            }
            Save();
            if (job is not null) JobChanged?.Invoke(job);
            await _client.CollectedAsync(id, _life.Token);
            lock (_lock) _tracked.Remove(id);
            Save();
            Notice?.Invoke(inShare is not null ? L.T("{0} is done, in the shared folder: {1}", Path.GetFileName(t.LocalSource), local)
                : L.T("{0} is back on this computer: {1}", Path.GetFileName(t.LocalSource), local), false);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception ex) when (ex is RemoteException or IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException)
        {
            Notice?.Invoke(L.T("The result of {0} could not come back yet ({1}): it waits on the server and is tried again at the next connection.",
                Path.GetFileName(t.LocalSource), ex.Message), true);
        }
        finally
        {
            lock (_lock) _downloading.Remove(id);
            _downloadGate.Release();
        }
    }

    private static bool IsInside(string path, string folder) =>
        string.Equals(Path.GetFullPath(Path.GetDirectoryName(path.TrimEnd('/', '\\')) ?? ""), Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ operations (IQueueEngine)

    private void Fire(Func<Task> call) => _ = Task.Run(async () =>
    {
        try { await call(); }
        catch (Exception ex) when (ex is RemoteException or HttpRequestException or IOException) { Notice?.Invoke(ex.Message, true); }
    });

    private (List<Guid> Server, List<QueueJob> Local) Split(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        lock (_lock)
        {
            var local = _jobs.Where(j => set.Contains(j.Id) && !IsServerJob(j)).ToList();
            return (set.Except(local.Select(j => j.Id)).ToList(), local);
        }
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var (server, local) = Split(ids);
        foreach (var j in local)
        {
            lock (_lock) { _jobs.Remove(j); _pending.RemoveAll(p => p.LocalId == j.Id); _home.Remove(j.Id); }
            JobRemoved?.Invoke(j.Id);
        }
        lock (_lock) foreach (var id in server) _tracked.Remove(id);
        Save();
        if (local.Count > 0) QueueChanged?.Invoke();
        if (server.Count > 0) Fire(() => _client.RemoveAsync(server));
    }

    public void Reset(IEnumerable<Guid> ids)
    {
        var (server, local) = Split(ids);
        foreach (var j in local.Where(j => j.Status == JobStatus.Error && _pending.Any(p => p.LocalId == j.Id)))
        {
            j.Status = JobStatus.Ready;
            j.Message = null;
            j.Stage = L.T("Waiting to be uploaded to the server");
            JobChanged?.Invoke(j);
        }
        if (local.Count > 0) _ = Task.Run(UploadLoopAsync);
        if (server.Count > 0) Fire(() => _client.ResetAsync(server));
    }

    public void Move(Guid id, int delta) => Fire(() => _client.MoveAsync(id, delta));

    public void UpdateSettings(IEnumerable<Guid> ids, Func<JobSettings, JobSettings> update)
    {
        var (server, local) = Split(ids);
        foreach (var j in local.Where(j => j.Status != JobStatus.Running))
        {
            j.Settings = update(j.Settings);
            lock (_lock) { var i = _pending.FindIndex(p => p.LocalId == j.Id); if (i >= 0) _pending[i] = _pending[i] with { Settings = j.Settings }; }
            JobChanged?.Invoke(j);
        }
        Save();
        foreach (var group in server.Select(id => Jobs.FirstOrDefault(j => j.Id == id)).OfType<QueueJob>().GroupBy(j => update(j.Settings)))
        {
            var g = group.Select(j => j.Id).ToList();
            Fire(() => _client.UpdateSettingsAsync(g, group.Key));
        }
    }

    public void RemoveByStatus(params JobStatus[] statuses) => Remove(Jobs.Where(j => statuses.Contains(j.Status)).Select(j => j.Id));

    public void Start() => Fire(() => _client.StartAsync());
    public void Stop() => Fire(() => _client.StopAsync());
    public void Pause() => Fire(() => _client.PauseAsync());
    public void Resume() => Fire(() => _client.ResumeAsync());

    /// <summary>The job's log: the server's, or this computer's note for an upload.</summary>
    public async Task<string> LogAsync(QueueJob job) =>
        IsServerJob(job) ? await _client.LogAsync(job.Id) : job.Message ?? job.Stage ?? "";

    public async Task<string> CommandAsync(QueueJob job) =>
        IsServerJob(job) ? await _client.CommandAsync(job.Id) : L.T("The command is built by the server once the file is uploaded.");

    /// <summary>A job of the server (not an upload waiting here nor a result already back).</summary>
    public bool OnServer(QueueJob job) { lock (_lock) return IsServerJob(job); }

    // ------------------------------------------------------------------ state file

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var st = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(_statePath), SettingsStore.Json);
            if (st is null) return;
            foreach (var (k, v) in st.Jobs) _tracked[k] = v;
            _pending.AddRange(st.Uploads);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { }
    }

    private void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(new StateFile(new(_tracked), [.. _pending]), SettingsStore.Json);
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        if (_listener is { } l) { try { await l; } catch (Exception) { } }
        Save();
        _client.Dispose();
        _life.Dispose();
    }
}
