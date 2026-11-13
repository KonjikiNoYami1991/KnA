using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KnA.Core.Encoding;
using KnA.Core.Queue;
using KnA.Core.Localization;
using KnA.Core.Settings;
using KnA.Core.Tools;

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

    public QueueService Queue { get; }
    public IDialogService? Dialogs { get; set; }
    public ObservableCollection<JobViewModel> Jobs { get; } = [];
    public ObservableCollection<HardwareRow> Hardware { get; } = [];

    /// <summary>Settings of the side panel form (used for new files and "apply to selected").</summary>
    public JobSettings FormSettings { get; set; }

    public IReadOnlyList<Preset> Presets => Queue.Presets.All;

    [ObservableProperty] private string? _updateBanner;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _queueStatus = L.T("Idle");
    [ObservableProperty] private string _hardwareInfo = L.T("Not detected yet.");
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _windowTitle = "KnA - Konjiki no Autoencoder";

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
        queue.JobChanged += j => Dispatcher.UIThread.Post(() => OnJobChanged(j));
        queue.JobRemoved += id => Dispatcher.UIThread.Post(() => OnJobRemoved(id));
        queue.QueueChanged += () => Dispatcher.UIThread.Post(OnQueueChanged);
        queue.QueueCompleted += action => Dispatcher.UIThread.Post(() =>
            PowerActions.Run(action, () => Dialogs?.CloseApp()));
        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        FormSettings = queue.Settings.Defaults;
        RefreshHardware();
        if (queue.Tools is not null && queue.Settings.Capabilities is null)
            _ = DetectHardwareAsync();
    }

    // ------------------------------------------------------------------ form & presets

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
        Queue.Presets.Delete(p.Name);
        FormSettings = FormSettings with { PresetName = null };
        OnPropertyChanged(nameof(Presets));
        FormReloaded?.Invoke();
    }

    public void SaveAsPreset(string name)
    {
        var p = Queue.Presets.Save(new Preset(name.Trim(), null, FormSettings with { PresetName = name.Trim() }));
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
        Queue.UpdateSettings(Jobs.Where(j => j.IsSelected).Select(j => j.Id), _ => s);
    }

    // ------------------------------------------------------------------ queue

    public void AddPaths(IEnumerable<string> paths, string? preset = null)
    {
        var settings = preset is not null && Queue.Presets.Get(preset) is { } p ? p.Settings with { PresetName = p.Name } : CurrentSettings();
        Queue.AddPaths(paths, settings, Queue.Settings.FolderExtensions);
    }

    public async Task OpenEditorAsync(JobViewModel j, string tab = "tracks")
    {
        if (Dialogs is not null) await Dialogs.ShowEditorAsync(this, j.Job, tab);
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
        if (Queue.IsPaused) Queue.Resume(); else Queue.Pause();
    }

    [RelayCommand] private void RemoveSelected() => Queue.Remove(Jobs.Where(j => j.IsSelected).Select(j => j.Id));
    [RelayCommand] private void RetrySelected() => Queue.Reset(Jobs.Where(j => j.IsSelected).Select(j => j.Id));
    [RelayCommand] private void ClearFinished() => Queue.RemoveByStatus(JobStatus.Done, JobStatus.Warning);

    [RelayCommand]
    private void SelectAll()
    {
        bool all = Jobs.Count > 0 && Jobs.All(j => j.IsSelected);
        foreach (var j in Jobs) j.IsSelected = !all;
    }

    public async Task ShowLogAsync(JobViewModel j)
    {
        if (Dialogs is null) return;
        var log = j.Job.Log;
        await Dialogs.ShowTextAsync(L.T("Log") + " — " + j.FileName, string.IsNullOrWhiteSpace(log) ? L.T("(empty — the log fills while the file is processed)") : log);
    }

    public async Task ShowCommandAsync(JobViewModel j)
    {
        if (Dialogs is null) return;
        string cmd;
        try { cmd = j.Job.CommandLine ?? await Queue.PreviewCommandAsync(j.Job); }
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
        var order = Queue.Jobs.Select(j => j.Id).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            if (!_byId.TryGetValue(order[i], out var vm)) continue;
            int cur = Jobs.IndexOf(vm);
            if (cur != i && cur >= 0) Jobs.Move(cur, i);
        }
        IsRunning = Queue.IsRunning;
        IsPaused = Queue.IsPaused;
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(PauseLabel));
        UpdateQueueStatus();
    }

    private void UpdateQueueStatus()
    {
        var all = Queue.Jobs;
        int done = all.Count(j => j.Status is JobStatus.Done or JobStatus.Warning);
        int ready = all.Count(j => j.Status == JobStatus.Ready);
        int failed = all.Count(j => j.Status is JobStatus.Error or JobStatus.Stopped);
        var cur = Queue.CurrentJob;
        QueueStatus = Queue.IsPaused ? L.T("Paused")
            : Queue.IsRunning ? $"{L.T("Encoding")} {done + 1}/{all.Count}{(cur is null ? "" : $" · {cur.Percent:0}%")}"
            : all.Count > 0 ? L.T("{0} ready · {1} done", ready, done) + (failed > 0 ? " · " + L.T("{0} failed", failed) : "") : L.T("Idle");
        WindowTitle = Queue.IsRunning && cur is not null ? $"{cur.Percent:0}% · KnA" : "KnA - Konjiki no Autoencoder";
    }

    // ------------------------------------------------------------------ hardware / settings

    [RelayCommand]
    private async Task DetectHardwareAsync()
    {
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
        var caps = Queue.Settings.Capabilities;
        if (caps is null)
        {
            HardwareInfo = Queue.Tools is null ? L.T("FFmpeg not found — open Settings.") : L.T("Not detected yet.");
            return;
        }
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
            if (b == EncoderBackend.VideoToolbox && !OperatingSystem.IsMacOS()) continue;
            if (b == EncoderBackend.Vaapi && !OperatingSystem.IsLinux()) continue;
            if (b == EncoderBackend.Amf && OperatingSystem.IsMacOS()) continue;
            Hardware.Add(new HardwareRow { Name = b.Short(), H264 = Cell(VideoCodec.H264, b), Hevc = Cell(VideoCodec.HEVC, b), Av1 = Cell(VideoCodec.AV1, b) });
        }
        HardwareInfo = $"{caps.Version.Replace("ffmpeg version ", "FFmpeg ").Split(" Copyright")[0]} · libass {(caps.HasLibass ? "✓" : "✕")}";
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

    /// <summary>Once a day at most: is a newer KnA published on GitHub?</summary>
    public async Task CheckUpdatesOnStartAsync()
    {
        var s = Queue.Settings;
        if (!s.CheckUpdatesOnStart || s.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(20)) return;
        try
        {
            var u = await AppUpdater.CheckAsync("desktop");
            s.LastUpdateCheck = DateTimeOffset.Now;
            Queue.SaveSettings();
            if (u.IsNewer) UpdateBanner = u.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
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
