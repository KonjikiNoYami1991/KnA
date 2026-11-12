using TnA.Core.Encoding;
using TnA.Core.Media;
using TnA.Core.Settings;
using TnA.Core.Tools;

namespace TnA.Core.Queue;

/// <summary>
/// The encoding queue, shared by the desktop app and the web server.
/// Thread-safe; events are raised on background threads (front-ends marshal them to their UI thread).
/// </summary>
public sealed class QueueService : IDisposable
{
    private readonly List<QueueJob> _jobs = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _probeGate = new(2);
    private CancellationTokenSource? _runCts;
    private ProcessGroup? _processes;
    private Task? _runTask;

    public SettingsStore Store { get; }
    public AppSettings Settings { get; private set; }
    public FfmpegTools? Tools { get; private set; }

    private volatile bool _running;
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

    public QueueService(SettingsStore store)
    {
        Store = store;
        Settings = store.Load();
        RefreshTools();
    }

    public IReadOnlyList<QueueJob> Jobs { get { lock (_lock) return [.. _jobs]; } }

    public QueueJob? Find(Guid id) { lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id); }

    public FfmpegTools? RefreshTools()
    {
        Tools = FfmpegLocator.Locate(Settings.FfmpegPath, Store.DataDirectory);
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
        var caps = await HardwareDetector.DetectAsync(tools.Ffmpeg, Settings.VaapiDevice, log, ct);
        Settings.Capabilities = caps;
        SaveSettings();
        return caps;
    }

    // ---------------------------------------------------------------- adding / editing

    public IReadOnlyList<QueueJob> AddPaths(IEnumerable<string> paths, JobSettings settings, IEnumerable<string>? folderExtensions = null)
    {
        var exts = (folderExtensions ?? Settings.FolderExtensions).Select(e => e.ToLowerInvariant()).ToHashSet();
        var files = new List<string>();
        foreach (var raw in paths)
        {
            var path = raw.Trim().Trim('"');
            if (string.IsNullOrEmpty(path)) continue;
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

    /// <summary>Files written by TnA itself ("Name [Profile …].ext") are skipped when importing folders.</summary>
    private static bool IsOwnOutput(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file);
        return n.EndsWith(']') && n.Contains(" [", StringComparison.Ordinal)
               && Profiles.All.Any(p => n.Contains("[" + p.Name, StringComparison.Ordinal));
    }

    private async Task ProbeInBackgroundAsync(QueueJob job)
    {
        if (Tools is not { } tools) return;
        await _probeGate.WaitAsync();
        try
        {
            job.Media = await new MediaProber(tools.Ffprobe).ProbeAsync(job.SourcePath);
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
        foreach (var j in removed) JobRemoved?.Invoke(j.Id);
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
            int k = Math.Clamp(i + delta, 0, _jobs.Count - 1);
            if (i < 0 || i == k) return;
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
        if (RefreshTools() is null) throw new InvalidOperationException("FFmpeg was not found. Configure it in Settings.");
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
                await JobRunner.RunAsync(next, ctx, ct);
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
        if (Tools is not { } tools) return "FFmpeg not found.";
        var media = job.Media ?? await new MediaProber(tools.Ffprobe).ProbeAsync(job.SourcePath);
        if (job.Settings.ProfileDefinition.IsKeyframes)
            return tools.Scxvid is null ? "ffmpeg … -vf select='gt(scene,0.3)' (scene detection)" : "ffmpeg … -f yuv4mpegpipe - | scxvid keyframes.txt";
        PreparedSubtitles? subs = null;
        if (job.Settings.EffectiveSubtitles == SubtitleMode.Hardsub && SubtitleSelector.Choose(media, job.Settings.UseSidecarSubtitles) is { } c)
            subs = c.IsBitmap ? new PreparedSubtitles(null, null, c.Stream!.Index) : new PreparedSubtitles("sub.ass", media.Attachments.Any(a => a.IsFont) ? "fonts" : null, null);
        var plan = CommandBuilder.Build(media, job.Settings, subs, new BuildOptions
        {
            OutputDirectory = Settings.OutputDirectory,
            VaapiDevice = Settings.VaapiDevice,
            ExtraArguments = JobRunner.SplitArgs(Settings.ExtraArguments),
            AppVersion = AppInfo.Version,
        });
        return plan.CommandLine(tools.Ffmpeg);
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _processes?.KillAll();
        _runCts?.Dispose();
        _probeGate.Dispose();
    }
}
