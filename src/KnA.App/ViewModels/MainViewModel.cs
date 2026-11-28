using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KnA.Core.Encoding;
using KnA.Core.Queue;
using KnA.Core.Localization;
using KnA.Core.Settings;
using KnA.Core.Tools;
using KnA.Core.Remote;

namespace KnA.App.ViewModels;

/// <summary>What the view provides to the view-model (file pickers, dialogs).</summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickFolderAsync();
    Task ShowTextAsync(string title, string text);
    Task ShowSettingsAsync(MainViewModel vm, string tab = "general");
    Task ShowEditorAsync(MainViewModel vm, QueueJob job, string tab);
    Task<Preset?> ShowWizardAsync(MainViewModel vm);
    Task<bool> ConfirmAsync(string title, string message);
    void CloseApp();
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<Guid, JobViewModel> _byId = [];

    /// <summary>This computer's engine: settings, presets, hardware and the local queue.</summary>
    public QueueService Queue { get; }
    /// <summary>The KnA server the queue runs on (client mode); null = this computer.</summary>
    public RemoteQueue? Remote { get; private set; }
    /// <summary>The queue shown: the server's in client mode, this computer's otherwise.</summary>
    public IQueueEngine Engine => (IQueueEngine?)Remote ?? Queue;
    public bool IsRemote => Remote is not null;
    private readonly LocalEditorEngine _localEditor;
    /// <summary>What the editor, wizard, AI assistant and OCR editor work with: this computer or the server.</summary>
    public IEditorEngine Editor { get; private set; }
    private readonly Action<QueueJob> _onJob;
    private readonly Action<Guid> _onRemoved;
    private readonly Action _onQueue;
    /// <summary>Watch folders run in the desktop app too (same settings as the web edition).</summary>
    public WatchFolderService Watch { get; }
    public IDialogService? Dialogs { get; set; }
    public ObservableCollection<JobViewModel> Jobs { get; } = [];
    public ObservableCollection<HardwareRow> Hardware { get; } = [];

    /// <summary>Settings of the side panel form (used for new files and "apply to selected").</summary>
    public JobSettings FormSettings { get; set; }

    /// <summary>The presets of the side panel: this computer's, or the server's (shared and the account's own).</summary>
    public IReadOnlyList<Preset> Presets => Editor.Presets;

    [ObservableProperty] private string? _updateBanner;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _queueStatus = L.T("Idle");
    [ObservableProperty] private string _hardwareInfo = L.T("Not detected yet.");
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _windowTitle = "KnA - Konjiki no Autoencoder";
    /// <summary>Where the work happens: "This computer" or the server's name (top bar, opens Settings → KnA server).</summary>
    [ObservableProperty] private string _engineLabel = L.T("This computer");
    /// <summary>What the server connection has to say (lost connection, results back home…); click to dismiss.</summary>
    [ObservableProperty] private string? _remoteNotice;

    public string Version => "v" + AppInfo.Version;
    public bool IsEmpty => Jobs.Count == 0;
    public string StartLabel => IsRunning ? L.T("Stop") : L.T("Start");
    public string PauseLabel => IsPaused ? L.T("Resume") : L.T("Pause");
    public string SettingsTarget => SelectedCount > 0 ? L.T("{0} selected", SelectedCount) : L.T("for new files");
    public string ApplyLabel => SelectedCount > 0 ? L.T("Apply to {0} selected", SelectedCount) : L.T("Apply to selected");

    /// <summary>Raised when the side panel must reload <see cref="FormSettings"/> (preset picked, wizard).</summary>
    public event Action? FormReloaded;

    public MainViewModel(QueueService queue)
    {
        Queue = queue;
        Editor = _localEditor = new LocalEditorEngine(queue);
        _onJob = j => Dispatcher.UIThread.Post(() => OnJobChanged(j));
        _onRemoved = id => Dispatcher.UIThread.Post(() => OnJobRemoved(id));
        _onQueue = () => Dispatcher.UIThread.Post(OnQueueChanged);
        Attach(queue);
        queue.QueueCompleted += action => Dispatcher.UIThread.Post(() =>
            PowerActions.Run(action, () => Dialogs?.CloseApp()));
        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        FormSettings = queue.Settings.Defaults;
        Watch = new WatchFolderService(queue);
        RefreshHardware();
        // The test follows the machine: first start, a new GPU or driver, updated encoder programs.
        if (queue.HardwareTestNeeded() is not null)
            _ = DetectHardwareAsync();
    }

    // ------------------------------------------------------------------ form & presets

    /// <summary>Presets changed elsewhere (preset manager).</summary>
    public void ReloadPresets() => OnPropertyChanged(nameof(Presets));

    public void ApplyPreset(Preset p)
    {
        FormSettings = p.Settings with { PresetName = p.Name };
        FormReloaded?.Invoke();
    }

    public async Task RunWizardAsync()
    {
        if (Dialogs is null) return;
        if (await Dialogs.ShowWizardAsync(this) is { } p) { OnPropertyChanged(nameof(Presets)); ApplyPreset(p); }
    }

    public async Task DeletePresetAsync(Preset p)
    {
        if (p.BuiltIn || Dialogs is null) return;
        if (!await Dialogs.ConfirmAsync(L.T("Delete preset"), L.T("Delete the preset \"{0}\"?", p.Name))) return;
        try { await Editor.DeletePresetAsync(p.Name); }
        catch (RemoteException ex) { await Dialogs.ShowTextAsync(L.T("Error"), ex.Message); return; }
        FormSettings = FormSettings with { PresetName = null };
        OnPropertyChanged(nameof(Presets));
        FormReloaded?.Invoke();
    }

    public async Task SaveAsPresetAsync(string name)
    {
        var p = await Editor.SavePresetAsync(new Preset(name.Trim(), null, FormSettings with { PresetName = name.Trim() }));
        OnPropertyChanged(nameof(Presets));
        ApplyPreset(p);
    }

    public JobSettings CurrentSettings() => FormSettings;

    [RelayCommand]
    private void SaveDefaults()
    {
        Queue.Settings.Defaults = CurrentSettings();
        Queue.SaveSettings();
    }

    public bool HasSelection => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ApplyToSelected()
    {
        var s = CurrentSettings();
        Engine.UpdateSettings(Jobs.Where(j => j.IsSelected).Select(j => j.Id), _ => s);
    }

    // ------------------------------------------------------------------ queue

    public void AddPaths(IEnumerable<string> paths, string? preset = null)
    {
        var settings = preset is not null && Editor.GetPreset(preset) is { } p ? p.Settings with { PresetName = p.Name } : CurrentSettings();
        // On a server: the files are uploaded by themselves and their results come back into the output folder.
        if (Remote is { } remote) remote.AddLocalPaths(paths, settings, Queue.Settings.FolderExtensions, Queue.Settings.OutputDirectory);
        else Queue.AddPaths(paths, settings, Queue.Settings.FolderExtensions);
    }

    /// <summary>
    /// Smart drop: videos, discs and folders are queued, subtitles/audio/fonts join the job they were dropped on
    /// (or the video with the same name), presets are imported. Rejects, duplicates and big folders are confirmed first.
    /// </summary>
    public async Task DropAsync(IReadOnlyList<string> paths, Guid? targetJob = null)
    {
        if (Remote is { } remote)
        {
            // On a server: videos and folders are uploaded and queued; subtitles, audio and fonts are uploaded and join
            // the job they were dropped on (or the server's video with the same name).
            var externals = paths.Where(DropClassifier.IsExternal).ToList();
            AddPaths(paths.Where(p => Directory.Exists(p) || MediaExtensions.IsVideo(p)));
            if (externals.Count == 0) return;
            try
            {
                var onServer = await Editor.ExternalFilesAsync(externals);
                var dropped = await remote.Client.DropAsync(onServer, targetJob, CurrentSettings());
                if (dropped.Messages.Count > 0 && Dialogs is not null) await Dialogs.ShowTextAsync("KnA", string.Join("\n", dropped.Messages));
            }
            catch (RemoteException ex) { if (Dialogs is not null) await Dialogs.ShowTextAsync(L.T("Error"), ex.Message); }
            return;
        }
        var plan = DropClassifier.Classify(paths, Queue, targetJob);
        if (Dialogs is not null && (plan.NeedsConfirmation || !plan.HasWork))
        {
            string Describe(DropItem i) => i.Kind switch
            {
                DropKind.Video => L.T("queued"),
                DropKind.Disc => L.T("disc: every title is queued"),
                DropKind.Folder => L.T("folder: {0} video(s), {1} disc(s)", i.Videos, i.Discs),
                DropKind.External => L.T("attached to its video"),
                DropKind.Preset => L.T("preset imported"),
                _ => i.Reason ?? i.Kind.ToString(),
            };
            var lines = string.Join("\n", plan.Items.Take(30).Select(i => $"• {Path.GetFileName(i.Path.TrimEnd('/', '\\'))} — {Describe(i)}"));
            if (plan.Items.Count > 30) lines += "\n…";
            if (!plan.HasWork) { await Dialogs.ShowTextAsync(L.T("Nothing to do."), lines); return; }
            if (!await Dialogs.ConfirmAsync("KnA", L.T("KnA will do this:") + "\n\n" + lines + "\n\n" + L.T("Continue?"))) return;
        }
        var r = await DropClassifier.ApplyAsync(plan, Queue, CurrentSettings());
        if (r.Presets > 0) { OnPropertyChanged(nameof(Presets)); FormReloaded?.Invoke(); }
        if (r.Messages.Count > 0 && Dialogs is not null) await Dialogs.ShowTextAsync("KnA", string.Join("\n", r.Messages));
    }

    public async Task OpenEditorAsync(JobViewModel j, string tab = "tracks")
    {
        if (Dialogs is null) return;
        // On a server the same editor works through its API (analysis, previews, OCR, AI run there).
        if (Remote is { } remote && !remote.OnServer(j.Job))
        {
            await Dialogs.ShowTextAsync(L.T("Editor"), L.T("This file is still on this computer: it can be edited once it is uploaded to the server."));
            return;
        }
        await Dialogs.ShowEditorAsync(this, j.Job, tab);
    }

    public static void OpenInBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    [RelayCommand]
    private async Task AddFiles()
    {
        if (Dialogs is null) return;
        AddPaths(await Dialogs.PickFilesAsync());
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        if (Dialogs is null) return;
        if (await Dialogs.PickFolderAsync() is { } folder) AddPaths([folder]);
    }

    [RelayCommand]
    private async Task StartStop()
    {
        if (Remote is { } remote)
        {
            if (remote.IsRunning) remote.Stop(); else remote.Start();
            return;
        }
        if (Queue.IsRunning)
        {
            await Queue.StopAsync();
            return;
        }
        if (Queue.RefreshTools() is null)
        {
                if (Dialogs is not null) await Dialogs.ShowSettingsAsync(this, "updates");
            if (Queue.RefreshTools() is null) return;
            return;
        }
        Queue.Start();
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (Remote is { } remote) { if (remote.IsPaused) remote.Resume(); else remote.Pause(); return; }
        if (Queue.IsPaused) Queue.Resume(); else Queue.Pause();
    }

    [RelayCommand] private void RemoveSelected() => Engine.Remove(Jobs.Where(j => j.IsSelected).Select(j => j.Id));
    [RelayCommand] private void RetrySelected() => Engine.Reset(Jobs.Where(j => j.IsSelected).Select(j => j.Id));
    [RelayCommand] private void ClearFinished() => Engine.RemoveByStatus(JobStatus.Done, JobStatus.Warning);

    [RelayCommand]
    private void SelectAll()
    {
        bool all = Jobs.Count > 0 && Jobs.All(j => j.IsSelected);
        foreach (var j in Jobs) j.IsSelected = !all;
    }

    public async Task ShowLogAsync(JobViewModel j)
    {
        if (Dialogs is null) return;
        string log;
        try { log = Remote is { } remote ? await remote.LogAsync(j.Job) : j.Job.Log; }
        catch (RemoteException ex) { log = ex.Message; }
        await Dialogs.ShowTextAsync(L.T("Log") + " — " + j.FileName, string.IsNullOrWhiteSpace(log) ? L.T("(empty — the log fills while the file is processed)") : log);
    }

    public async Task ShowCommandAsync(JobViewModel j)
    {
        if (Dialogs is null) return;
        string cmd;
        try { cmd = Remote is { } remote ? await remote.CommandAsync(j.Job) : j.Job.CommandLine ?? await Queue.PreviewCommandAsync(j.Job); }
        catch (Exception ex) { cmd = L.T("Cannot build the command: {0}", ex.Message); }
        await Dialogs.ShowTextAsync(L.T("FFmpeg command"), cmd);
    }

    internal void OnSelectionChanged()
    {
        SelectedCount = Jobs.Count(j => j.IsSelected);
        OnPropertyChanged(nameof(SettingsTarget));
        OnPropertyChanged(nameof(ApplyLabel));
        ApplyToSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnJobChanged(QueueJob job)
    {
        if (!_byId.TryGetValue(job.Id, out var vm))
        {
            vm = new JobViewModel(job, this);
            _byId[job.Id] = vm;
            Jobs.Add(vm);
        }
        vm.Refresh();
        UpdateQueueStatus();
    }

    private void OnJobRemoved(Guid id)
    {
        if (_byId.Remove(id, out var vm)) Jobs.Remove(vm);
        OnSelectionChanged();
        UpdateQueueStatus();
    }

    private void OnQueueChanged()
    {
        // Mirror the service order (moves).
        var order = Engine.Jobs.Select(j => j.Id).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            if (!_byId.TryGetValue(order[i], out var vm)) continue;
            int cur = Jobs.IndexOf(vm);
            if (cur != i && cur >= 0) Jobs.Move(cur, i);
        }
        IsRunning = Engine.IsRunning;
        IsPaused = Engine.IsPaused;
        UpdateEngineLabel();
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(PauseLabel));
        UpdateQueueStatus();
    }

    private void UpdateQueueStatus()
    {
        var all = Engine.Jobs;
        int done = all.Count(j => j.Status is JobStatus.Done or JobStatus.Warning);
        int ready = all.Count(j => j.Status == JobStatus.Ready);
        int failed = all.Count(j => j.Status is JobStatus.Error or JobStatus.Stopped);
        var cur = Engine.CurrentJob;
        QueueStatus = Remote is { Connected: false } ? L.T("KnA server not reachable: retrying…")
            : Remote is { BusyWithOthers: true, IsPaused: false } ? L.T("The server is encoding another account's file") + (ready > 0 ? " · " + L.T("{0} ready · {1} done", ready, done) : "")
            : Engine.IsPaused ? L.T("Paused")
            : Engine.IsRunning ? $"{L.T("Encoding")} {done + 1}/{all.Count}{(cur is null ? "" : $" · {cur.Percent:0}%")}"
            : all.Count > 0 ? L.T("{0} ready · {1} done", ready, done) + (failed > 0 ? " · " + L.T("{0} failed", failed) : "") : L.T("Idle");
        WindowTitle = Engine.IsRunning && cur is not null ? $"{cur.Percent:0}% · KnA" : "KnA - Konjiki no Autoencoder";
    }

    // ------------------------------------------------------------------ this computer or a KnA server

    private void Attach(IQueueEngine e)
    {
        e.JobChanged += _onJob;
        e.JobRemoved += _onRemoved;
        e.QueueChanged += _onQueue;
    }

    private void Detach(IQueueEngine e)
    {
        e.JobChanged -= _onJob;
        e.JobRemoved -= _onRemoved;
        e.QueueChanged -= _onQueue;
    }

    /// <summary>The list shows <see cref="Engine"/>'s jobs again (after switching where the work happens).</summary>
    private void ReloadJobs()
    {
        _byId.Clear();
        Jobs.Clear();
        foreach (var j in Engine.Jobs) OnJobChanged(j);
        OnSelectionChanged();
        OnQueueChanged();
        RefreshHardware();
        OnPropertyChanged(nameof(IsRemote));
    }

    private void UpdateEngineLabel() =>
        EngineLabel = Remote is { } r
            ? (r.Connected ? "🌐 " : "⚠ ") + (r.Server?.Name ?? r.Client.BaseUri.Host) + " · " + r.Client.User
            : "🖥 " + L.T("This computer");

    /// <summary>
    /// Encodes on a KnA server from now on: checks it (version, account), mirrors its queue. Throws
    /// <see cref="RemoteException"/> with a plain reason when it can't; this computer's queue stays as it was.
    /// </summary>
    public async Task ConnectAsync(RemoteServerSettings server, string password, CancellationToken ct = default)
    {
        var client = new KnaClient(server.Url ?? "", server.User ?? "", password, server.PinnedCertificate);
        var paths = new ServerPaths(client, Queue.Store.DataDirectory);
        var remote = new RemoteQueue(client, Queue.Store.DataDirectory, paths);
        var editor = new RemoteEditorEngine(client, Path.Combine(Queue.Store.DataDirectory, "remote-cache"), paths);
        try { await remote.ConnectAsync(ct); await editor.RefreshAsync(ct); }
        catch { await remote.DisposeAsync(); throw; }
        remote.Notice += (text, _) => Dispatcher.UIThread.Post(() => { RemoteNotice = text; UpdateQueueStatus(); UpdateEngineLabel(); RefreshHardware(); });
        await DisconnectAsync(keepSetting: true);
        Detach(Queue);
        Remote = remote;
        Editor = editor;
        Attach(remote);
        ReloadJobs();
        OnPropertyChanged(nameof(Presets));
        FormReloaded?.Invoke();
    }

    /// <summary>Back to this computer (one click): the server keeps its jobs; results come back at the next connection.</summary>
    public async Task DisconnectAsync(bool keepSetting = false)
    {
        if (Remote is not { } remote) return;
        Detach(remote);
        Remote = null;
        (Editor as RemoteEditorEngine)?.ClearCache();
        Editor = _localEditor;
        Attach(Queue);
        await remote.DisposeAsync();
        ReloadJobs();
        OnPropertyChanged(nameof(Presets));
        FormReloaded?.Invoke();
        if (!keepSetting)
        {
            Queue.Settings.Remote = Queue.Settings.Remote with { Enabled = false };
            Queue.SaveSettings();
        }
    }

    /// <summary>At start: the server chosen last time, with the password from the system's keychain.</summary>
    public async Task ConnectSavedServerAsync()
    {
        var rs = Queue.Settings.Remote;
        if (!rs.Enabled || string.IsNullOrWhiteSpace(rs.Url) || string.IsNullOrWhiteSpace(rs.User)) return;
        string? password = null;
        try { password = SecretStores.ForThisSystem().Read(SecretStores.Key(rs.Url, rs.User)); }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception) { }
        if (password is null)
        {
            RemoteNotice = L.T("Encoding on this computer: the password of the KnA server {0} is not in the system's keychain. Type it in Settings → KnA server.", rs.Url);
            return;
        }
        EngineLabel = "⏳ " + rs.Url;
        try { await ConnectAsync(rs, password); }
        catch (RemoteException ex)
        {
            RemoteNotice = L.T("Encoding on this computer meanwhile: {0}", ex.Message);
            UpdateEngineLabel();
        }
    }

    [RelayCommand] private void DismissNotice() => RemoteNotice = null;

    [RelayCommand]
    private async Task OpenServerSettings()
    {
        if (Dialogs is null) return;
        await Dialogs.ShowSettingsAsync(this, "server");
        Queue.RefreshTools();
        RefreshHardware();
        OnPropertyChanged(nameof(Presets));
    }

    // ------------------------------------------------------------------ hardware / settings

    [RelayCommand]
    private async Task DetectHardwareAsync()
    {
        if (Remote is not null) { RefreshHardware(); return; }   // the server tests its own hardware
        IsDetecting = true;
        HardwareInfo = L.T("Testing…");
        try
        {
            var progress = new Progress<string>(l => HardwareInfo = l.Trim());
            await Task.Run(() => Queue.DetectHardwareAsync(progress));
        }
        catch (Exception ex)
        {
            HardwareInfo = ex.Message;
        }
        finally
        {
            IsDetecting = false;
            RefreshHardware();
            FormReloaded?.Invoke();
        }
    }

    private void RefreshHardware()
    {
        Hardware.Clear();
        // Client mode: the hardware that encodes is the server's.
        var caps = Remote is { } remote ? remote.Capabilities : Queue.Settings.Capabilities;
        if (Remote is { Server: { } info })
        {
            if (caps is not null) FillHardwareTable(caps, localOs: false);
            HardwareInfo = L.T("KnA server \"{0}\" · KnA {1} · {2}", info.Name, info.Version, info.Platform)
                           + (caps is null ? "\n" + L.T("Its hardware has not been tested yet (its web page: Settings → Hardware).")
                               : "\n" + caps.Version.Replace("ffmpeg version ", "FFmpeg ").Split(" Copyright")[0]);
            return;
        }
        if (caps is null)
        {
            HardwareInfo = Queue.Tools is null ? L.T("FFmpeg not found — open Settings.") : L.T("Not detected yet.");
            return;
        }
        FillHardwareTable(caps);
        LocalHardwareInfo(caps);
    }

    private void FillHardwareTable(FfmpegCapabilities caps, bool localOs = true)
    {
        HardwareCell Cell(VideoCodec c, EncoderBackend b)
        {
            var e = caps.Find(c, b);
            if (e is null) return new HardwareCell { Text = "—", Ok = false };
            return new HardwareCell
            {
                Text = e.Works10Bit ? "10-bit" : e.Works8Bit ? "8-bit" : "✕",
                Ok = e.Usable,
                Tooltip = e.Encoder + (e.Error is null ? "" : ": " + e.Error),
            };
        }
        foreach (var b in VideoEncoders.AllBackends)
        {
            if (b == EncoderBackend.Vulkan && !caps.Encoders.Any(e => e.Backend == b && e.Compiled)) continue;
            // A server's table: the backends its own detection found (its system may differ from this one).
            if (!localOs && !caps.Encoders.Any(e => e.Backend == b && e.Compiled)) continue;
            if (localOs && b == EncoderBackend.VideoToolbox && !OperatingSystem.IsMacOS()) continue;
            if (localOs && b == EncoderBackend.Vaapi && !OperatingSystem.IsLinux()) continue;
            if (localOs && b == EncoderBackend.Amf && OperatingSystem.IsMacOS()) continue;
            Hardware.Add(new HardwareRow { Name = b.Short(), H264 = Cell(VideoCodec.H264, b), Hevc = Cell(VideoCodec.HEVC, b), Av1 = Cell(VideoCodec.AV1, b) });
        }
    }

    private void LocalHardwareInfo(FfmpegCapabilities caps)
    {
        // The table shows H.264/HEVC/AV1; the other codecs that work are listed with their backends.
        var others = caps.Encoders.Where(e => e.Usable && e.Codec is not (VideoCodec.H264 or VideoCodec.HEVC or VideoCodec.AV1))
            .GroupBy(e => e.Codec).Select(g => $"{g.Key.Short()} ({string.Join("/", g.Select(e => e.Backend.Short()).Distinct())})");
        var main = caps.Main;
        string aac = Queue.Tools?.Qaac is not null ? "qaac" : caps.Builds.Any(b => b.Has("libfdk_aac")) ? "libfdk_aac" : caps.Builds.Any(b => b.Has("aac_at")) ? "AudioToolbox" : Queue.Tools?.Fdkaac is not null ? "fdkaac" : "FFmpeg";
        HardwareInfo = $"{caps.Version.Replace("ffmpeg version ", "FFmpeg ").Split(" Copyright")[0]}"
                       + (caps.Builds.Count > 1 ? " + " + L.T("{0} custom build(s)", caps.Builds.Count - 1) : "")
                       + $" · libass {(caps.HasLibass ? "✓" : "✕")} · libplacebo {(caps.HasLibplacebo ? "✓" : "✕")}"
                       + $" · Blu-ray {(main?.HasBluray == true ? "✓" : "✕")} · DVD {(main?.HasDvdVideo == true ? "✓" : "✕")} · AAC {aac}"
                       + $" · CPU {KnA.Core.Tools.CpuFeatures.Description}"
                       + (KnA.Core.Queue.JobRunner.RigayaFor(Queue.Tools) is { Count: > 0 } rig
                           ? " · rigaya: " + string.Join(", ", rig.Keys.Select(KnA.Core.Encoding.RigayaEncoders.DisplayName)) : "")
                       + "\n" + L.T("Also: {0}", string.Join(", ", others));
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (Dialogs is null) return;
        await Dialogs.ShowSettingsAsync(this);
        Queue.RefreshTools();
        RefreshHardware();
        OnPropertyChanged(nameof(Presets));
    }

    /// <summary>
    /// Once a day at most (at start and while KnA stays open): a newer KnA is installed by itself when the program
    /// folder may be written, otherwise the banner only tells about it.
    /// </summary>
    public async Task CheckUpdatesOnStartAsync()
    {
        var s = Queue.Settings;
        if (!s.CheckUpdatesOnStart || s.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(20)) return;
        try
        {
            var r = await Task.Run(() => AppUpdater.AutoUpdateAsync("desktop", Queue.Store.DataDirectory, s.AutoInstallUpdates));
            s.LastUpdateCheck = DateTimeOffset.Now;
            Queue.SaveSettings();
            if (r.State != AutoUpdateState.UpToDate) UpdateBanner = r.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException) { }
    }

    [RelayCommand]
    private async Task OpenUpdates()
    {
        UpdateBanner = null;
        if (Dialogs is not null) await Dialogs.ShowSettingsAsync(this, "updates");
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var actual = Avalonia.Application.Current?.ActualThemeVariant;
        var next = actual == Avalonia.Styling.ThemeVariant.Dark ? "Light" : "Dark";
        Queue.Settings.Theme = next;
        Queue.SaveSettings();
        App.ApplyTheme(next);
    }
}
