using System.Text.Json;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.Core.Queue;

/// <summary>What the editors need to show for a job: the effective track plan and the validation result.</summary>
public sealed record JobAnalysis(
    MediaInfo Source,
    IReadOnlyDictionary<string, MediaInfo> External,
    IReadOnlyList<TrackPlan> Tracks,
    bool TracksAreDefault,
    IReadOnlyList<ValidationIssue> Issues,
    string Container,
    IReadOnlyList<string> AllowedContainers,
    string HdrSummary,
    string? CommandLine,
    TimeSpan OutputDuration);

/// <summary>
/// The encoding queue, shared by the desktop app and the web server.
/// Thread-safe; events are raised on background threads (front-ends marshal them to their UI thread).
/// The queue is saved to disk and restored at the next start.
/// </summary>
public sealed class QueueService : IDisposable
{
    private readonly List<QueueJob> _jobs = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _probeGate = new(2);
    private readonly Timer _saveTimer;
    private CancellationTokenSource? _runCts;
    private ProcessGroup? _processes;
    private Task? _runTask;
    private volatile bool _running;

    public SettingsStore Store { get; }
    public AppSettings Settings { get; private set; }
    public FfmpegTools? Tools { get; private set; }
    public PresetStore Presets { get; }
    public PreviewService Preview { get; }
    public ComponentManager Components { get; }

    public bool IsRunning => _running;
    public bool IsPaused { get; private set; }
    public QueueJob? CurrentJob { get; private set; }

    /// <summary>A job was added, removed, or changed state/progress.</summary>
    public event Action<QueueJob>? JobChanged;
    public event Action<Guid>? JobRemoved;
    /// <summary>Running/paused state or the job list changed.</summary>
    public event Action? QueueChanged;
    /// <summary>The whole queue finished (not raised when stopped by the user).</summary>
    public event Action<PostQueueAction>? QueueCompleted;

    public QueueService(SettingsStore store, bool restoreQueue = true)
    {
        Store = store;
        Settings = store.Load();
        store.CleanTemp();
        ChunkedEncoder.CleanOld(store);
        RefreshTools();
        Presets = new PresetStore(store.DataDirectory);
        Preview = new PreviewService(this);
        Components = new ComponentManager(store.DataDirectory);
        _saveTimer = new Timer(_ => SaveQueue(), null, Timeout.Infinite, Timeout.Infinite);
        JobChanged += _ => ScheduleSave();
        QueueChanged += ScheduleSave;
        JobRemoved += _ => ScheduleSave();
        if (restoreQueue && Settings.RestoreQueue) RestoreQueue();
    }

    public IReadOnlyList<QueueJob> Jobs { get { lock (_lock) return [.. _jobs]; } }

    public QueueJob? Find(Guid id) { lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id); }

    public FfmpegTools? RefreshTools()
    {
        Tools = FfmpegLocator.Locate(Settings.FfmpegPath, Store.DataDirectory, Settings.ToolPaths);
        return Tools;
    }

    public void SaveSettings(AppSettings? settings = null)
    {
        if (settings is not null) Settings = settings;
        Store.Save(Settings);
    }

    public async Task<FfmpegCapabilities?> DetectHardwareAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (RefreshTools() is not { } tools) return null;
        var caps = await HardwareDetector.DetectAsync(tools.Ffmpeg, Settings.CustomFfmpegBuilds.Select(ResolveFfmpegExe).ToList(),
            Settings.VaapiDevice, log, ct);
        Settings.Capabilities = caps;
        SaveSettings();
        return caps;
    }

    /// <summary>A custom build may be given as the folder that contains ffmpeg.</summary>
    public static string ResolveFfmpegExe(string path) =>
        Directory.Exists(path) ? Path.Combine(path, FfmpegLocator.ExeName("ffmpeg")) : path;

    // ---------------------------------------------------------------- adding / editing

    public IReadOnlyList<QueueJob> AddPaths(IEnumerable<string> paths, JobSettings settings, IEnumerable<string>? folderExtensions = null)
    {
        var exts = (folderExtensions ?? Settings.FolderExtensions).Select(e => e.ToLowerInvariant()).ToHashSet();
        var files = new List<string>();
        foreach (var raw in paths)
        {
            var path = raw.Trim().Trim('"');
            if (string.IsNullOrEmpty(path)) continue;
            try
            {
                if (Directory.Exists(path))
                {
                    files.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Where(f => !IsOwnOutput(f))
                        .Order(StringComparer.OrdinalIgnoreCase));
                }
                else if (File.Exists(path) && MediaExtensions.IsVideo(path))
                {
                    files.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: skip */ }
        }

        var added = new List<QueueJob>();
        lock (_lock)
        {
            foreach (var f in files)
            {
                var job = new QueueJob { SourcePath = Path.GetFullPath(f), Settings = settings };
                _jobs.Add(job);
                added.Add(job);
            }
        }
        foreach (var j in added)
        {
            JobChanged?.Invoke(j);
            _ = ProbeInBackgroundAsync(j);
        }
        if (added.Count > 0) QueueChanged?.Invoke();
        return added;
    }

    /// <summary>Files written by KnA itself ("Name [Profile …].ext") are skipped when importing folders.</summary>
    private static bool IsOwnOutput(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file);
        return n.EndsWith(']') && n.Contains(" [", StringComparison.Ordinal)
               && Profiles.All.Any(p => n.Contains("[" + p.EnglishName, StringComparison.Ordinal) || n.Contains("[" + p.Name, StringComparison.Ordinal));
    }

    private async Task ProbeInBackgroundAsync(QueueJob job)
    {
        if (Tools is not { } tools) return;
        await _probeGate.WaitAsync();
        try
        {
            job.Media = await MediaProber.For(tools).ProbeAsync(job.SourcePath);
            job.ProbeError = null;
        }
        catch (Exception ex)
        {
            job.ProbeError = ex.Message;
        }
        finally
        {
            _probeGate.Release();
        }
        JobChanged?.Invoke(job);
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        List<QueueJob> removed;
        lock (_lock)
        {
            removed = _jobs.Where(j => set.Contains(j.Id) && j != CurrentJob).ToList();
            foreach (var j in removed) _jobs.Remove(j);
        }
        foreach (var j in removed)
        {
            Preview.Forget(j.Id);
            JobRemoved?.Invoke(j.Id);
        }
        if (removed.Count > 0) QueueChanged?.Invoke();
    }

    public void RemoveByStatus(params JobStatus[] statuses) =>
        Remove(Jobs.Where(j => statuses.Contains(j.Status)).Select(j => j.Id));

    public void UpdateSettings(IEnumerable<Guid> ids, Func<JobSettings, JobSettings> update)
    {
        foreach (var id in ids)
        {
            if (Find(id) is not { } j || j == CurrentJob) continue;
            j.Settings = update(j.Settings);
            if (j.Status != JobStatus.Running) j.ResetForRun();
            JobChanged?.Invoke(j);
        }
    }

    /// <summary>Replaces the per-file edits (trim, external files, track plan). External files are probed.</summary>
    public async Task<JobAnalysis> SetEditAsync(Guid id, JobEdit edit, CancellationToken ct = default, JobSettings? settings = null)
    {
        var j = Find(id) ?? throw new KeyNotFoundException();
        if (j == CurrentJob) throw new InvalidOperationException(L.T("The file is being processed."));
        j.Edit = edit;
        if (settings is not null) j.Settings = settings;
        if (j.Status != JobStatus.Ready) j.ResetForRun();
        var analysis = await AnalyzeAsync(j, ct);
        JobChanged?.Invoke(j);
        return analysis;
    }

    /// <summary>Probes the files (when needed) and returns the effective plan, validation and command.</summary>
    public async Task<JobAnalysis> AnalyzeAsync(QueueJob j, CancellationToken ct = default)
    {
        var tools = Tools ?? throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        var prober = MediaProber.For(tools);
        j.Media ??= await prober.ProbeAsync(j.SourcePath, ct);
        JobRunner.EnsureFrameserverScript(j, Store);
        foreach (var gone in j.ExternalMedia.Keys.Except(j.Edit.ExternalFiles).Where(k => j.Edit.Tracks?.All(t => t.File != k) ?? true).ToList())
            j.ExternalMedia.Remove(gone);
        await JobRunner.ProbeExternalsAsync(j, prober, ct);

        var edit = j.Edit;
        bool isDefault = edit.Tracks is null;
        var tracks = edit.Tracks ?? TrackPlanner.Default(j.Media, j.Settings, frameserver: JobRunner.FrameserverMedia(j));
        // Offer the streams of external files that are not in the plan yet.
        var extra = edit.ExternalFiles.Where(f => tracks.All(t => t.File != f))
            .SelectMany(f => TrackPlanner.ForExternal(f, j.ExternalMedia.GetValueOrDefault(f), include: false));
        tracks = [.. tracks, .. extra];

        var p = j.Settings.ProfileDefinition;
        var ctx = JobRunner.BuildContext(j, Settings, tools) with { Edit = edit with { Tracks = tracks } };
        if (p.IsDemux)
        {
            var d = DemuxBuilder.Build(j.Media, j.Settings, edit, ctx.Options, forcedFolder: Path.Combine(j.Folder, "(demux)"));
            j.Issues = d.Issues;
            return new JobAnalysis(j.Media, j.ExternalMedia, tracks, isDefault, d.Issues, "", [], "", d.Plan.CommandLine(tools.Ffmpeg), j.Media.Duration);
        }
        if (p.IsKeyframes)
            return new JobAnalysis(j.Media, j.ExternalMedia, tracks, isDefault, [], "txt", [], "", null, j.Media.Duration);

        var compiled = CommandBuilder.Compile(ctx with { ForcedOutputPath = Path.Combine(j.Folder, "(output)." + j.Settings.EffectiveContainer) });
        j.Issues = compiled.Issues;
        j.HdrSummary = compiled.Hdr.Summary;
        return new JobAnalysis(j.Media, j.ExternalMedia, tracks, isDefault, compiled.Issues, compiled.Container,
            Containers.Allowed(j.Settings), compiled.Hdr.Summary,
            compiled.Plan.Arguments.Count > 0 ? compiled.Plan.CommandLine(tools.Ffmpeg) : null, compiled.OutputDuration);
    }

    /// <summary>Marks finished/failed/stopped jobs as ready again.</summary>
    public void Reset(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
            if (Find(id) is { } j && j != CurrentJob && j.Status != JobStatus.Ready)
            {
                j.ResetForRun();
                JobChanged?.Invoke(j);
            }
    }

    public void Move(Guid id, int delta)
    {
        lock (_lock)
        {
            int i = _jobs.FindIndex(j => j.Id == id);
            if (i < 0) return;
            int k = Math.Clamp(i + delta, 0, _jobs.Count - 1);
            if (i == k) return;
            var j = _jobs[i];
            _jobs.RemoveAt(i);
            _jobs.Insert(k, j);
        }
        QueueChanged?.Invoke();
    }

    // ---------------------------------------------------------------- running

    public void Start()
    {
        if (IsRunning) return;
        if (RefreshTools() is null) throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        _runCts = new CancellationTokenSource();
        _processes = new ProcessGroup();
        IsPaused = false;
        _running = true;
        var ct = _runCts.Token;
        _runTask = Task.Run(() => RunLoopAsync(ct));
        QueueChanged?.Invoke();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        bool completed = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                QueueJob? next;
                lock (_lock) next = _jobs.FirstOrDefault(j => j.Status == JobStatus.Ready);
                if (next is null) { completed = true; break; }
                CurrentJob = next;
                QueueChanged?.Invoke();
                var ctx = new RunnerContext(Tools!, Settings, Store, _processes!, j => JobChanged?.Invoke(j));
                try { await JobRunner.RunAsync(next, ctx, ct); }
                catch (Exception ex)
                {
                    // JobRunner handles its own errors; this is the last safety net so the queue never dies.
                    Store.WriteCrashLog(ex, "queue");
                    next.Status = JobStatus.Error;
                    next.Message = ex.Message;
                    JobChanged?.Invoke(next);
                }
            }
        }
        finally
        {
            CurrentJob = null;
            IsPaused = false;
            _running = false;
            QueueChanged?.Invoke();
            if (completed) QueueCompleted?.Invoke(Settings.PostAction);
        }
    }

    public async Task StopAsync()
    {
        _runCts?.Cancel();
        _processes?.KillAll();
        if (_runTask is { } t) await t.ConfigureAwait(false);
    }

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        _processes?.Pause();
        IsPaused = true;
        if (CurrentJob is { } j) { j.Status = JobStatus.Paused; JobChanged?.Invoke(j); }
        QueueChanged?.Invoke();
    }

    public void Resume()
    {
        if (!IsPaused) return;
        _processes?.Resume();
        IsPaused = false;
        if (CurrentJob is { } j) { j.Status = JobStatus.Running; JobChanged?.Invoke(j); }
        QueueChanged?.Invoke();
    }

    /// <summary>Command preview for a job without running it.</summary>
    public async Task<string> PreviewCommandAsync(QueueJob job)
    {
        if (Tools is null) return L.T("FFmpeg was not found. Configure it in Settings.");
        var a = await AnalyzeAsync(job);
        var text = a.CommandLine ?? (job.Settings.ProfileDefinition.IsKeyframes
            ? (Tools.Scxvid is null ? "ffmpeg … -vf select='gt(scene,0.3)' (scene detection)" : "ffmpeg … -f yuv4mpegpipe - | scxvid keyframes.txt")
            : "");
        if (a.Issues.Count > 0) text += "\n\n" + string.Join("\n", a.Issues.Select(i => (i.IsError ? "✖ " : "⚠ ") + i.Message));
        return text;
    }

    // ---------------------------------------------------------------- persistence

    private sealed record SavedJob(string SourcePath, JobSettings Settings, JobEdit Edit, JobStatus Status, string? OutputPath, string? Message);

    private string QueuePath => Path.Combine(Store.DataDirectory, "queue.json");

    private void ScheduleSave() => _saveTimer.Change(1000, Timeout.Infinite);

    public void SaveQueue()
    {
        try
        {
            var data = Jobs.Select(j => new SavedJob(j.SourcePath, j.Settings, j.Edit,
                j.Status is JobStatus.Running or JobStatus.Paused ? JobStatus.Ready : j.Status, j.OutputPath, j.Message)).ToList();
            var tmp = QueuePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, SettingsStore.Json));
            File.Move(tmp, QueuePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void RestoreQueue()
    {
        try
        {
            if (!File.Exists(QueuePath)) return;
            var saved = JsonSerializer.Deserialize<List<SavedJob>>(File.ReadAllText(QueuePath), SettingsStore.Json) ?? [];
            lock (_lock)
            {
                foreach (var s in saved.Where(s => File.Exists(s.SourcePath)))
                {
                    var j = new QueueJob { SourcePath = s.SourcePath, Settings = s.Settings, Edit = s.Edit ?? new JobEdit() };
                    j.Status = s.Status;
                    j.OutputPath = s.OutputPath;
                    j.Message = s.Message;
                    _jobs.Add(j);
                }
            }
            foreach (var j in Jobs) _ = ProbeInBackgroundAsync(j);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            try { File.Move(QueuePath, QueuePath + ".broken", overwrite: true); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _processes?.KillAll();
        _saveTimer.Dispose();
        SaveQueue();
        _runCts?.Dispose();
        _probeGate.Dispose();
    }
}
