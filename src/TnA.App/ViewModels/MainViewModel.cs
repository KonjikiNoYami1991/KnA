using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TnA.Core.Encoding;
using TnA.Core.Queue;
using TnA.Core.Tools;

namespace TnA.App.ViewModels;

/// <summary>What the view provides to the view-model (file pickers, dialogs).</summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickFolderAsync();
    Task ShowTextAsync(string title, string text);
    Task ShowSettingsAsync(MainViewModel vm);
    Task<bool> ConfirmAsync(string title, string message);
    void CloseApp();
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<Guid, JobViewModel> _byId = [];
    private bool _loadingForm;

    public QueueService Queue { get; }
    public IDialogService? Dialogs { get; set; }
    public ObservableCollection<JobViewModel> Jobs { get; } = [];
    public ObservableCollection<HardwareRow> Hardware { get; } = [];

    public IReadOnlyList<ProfileDefinition> ProfileOptions { get; } = Profiles.All;
    public IReadOnlyList<Option<QualityLevel>> QualityOptions => OptionLists.Qualities;
    public IReadOnlyList<Option<int>> ResolutionOptions => OptionLists.Resolutions;
    public IReadOnlyList<Option<SubtitleMode>> SubtitleOptions => OptionLists.SubtitleModes;

    [ObservableProperty] private IReadOnlyList<Option<VideoCodec>> _codecOptions = [];
    [ObservableProperty] private IReadOnlyList<BackendOption> _backendOptions = [];

    [ObservableProperty] private ProfileDefinition _selectedProfile = Profiles.UniversalMkv;
    [ObservableProperty] private Option<VideoCodec>? _selectedCodec;
    [ObservableProperty] private BackendOption? _selectedBackend;
    [ObservableProperty] private Option<QualityLevel>? _selectedQuality;
    [ObservableProperty] private Option<int>? _selectedResolution;
    [ObservableProperty] private Option<SubtitleMode>? _selectedSubtitles;
    [ObservableProperty] private bool _tenBit = true;
    [ObservableProperty] private bool _tenBitPossible;
    [ObservableProperty] private bool _useSidecar = true;
    [ObservableProperty] private string _backendNote = "";

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _queueStatus = "Idle";
    [ObservableProperty] private string _hardwareInfo = "Not detected yet.";
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _windowTitle = "TnA - Tanoshimi no Autohardsubber";

    public string Version => "v" + AppInfo.Version;
    public bool IsEmpty => Jobs.Count == 0;
    public string StartLabel => IsRunning ? "Stop" : "Start";
    public string PauseLabel => IsPaused ? "Resume" : "Pause";
    public string SettingsTarget => SelectedCount > 0 ? $"{SelectedCount} selected" : "for new files";
    public string ApplyLabel => SelectedCount > 0 ? $"Apply to {SelectedCount} selected" : "Apply to selected";
    public bool ShowCodec => SelectedProfile.AllowsCodecChoice;
    public bool ShowEncoder => SelectedProfile.EncodesVideo || SelectedProfile.Kind == ProfileKind.RemuxMp4;
    public bool ShowQuality => SelectedProfile.UsesQuality;
    public bool ShowResolution => SelectedProfile.UsesResolution;
    public bool ShowSubtitles => SelectedProfile.UsesSubtitleMode;
    public bool ShowSidecar => ShowSubtitles && SelectedSubtitles?.Value == SubtitleMode.Hardsub;
    public bool ShowTenBit => SelectedProfile.EncodesVideo;

    public MainViewModel(QueueService queue)
    {
        Queue = queue;
        queue.JobChanged += j => Dispatcher.UIThread.Post(() => OnJobChanged(j));
        queue.JobRemoved += id => Dispatcher.UIThread.Post(() => OnJobRemoved(id));
        queue.QueueChanged += () => Dispatcher.UIThread.Post(OnQueueChanged);
        queue.QueueCompleted += action => Dispatcher.UIThread.Post(() =>
            PowerActions.Run(action, () => Dialogs?.CloseApp()));
        Jobs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        LoadForm(queue.Settings.Defaults);
        RefreshHardware();
        if (queue.Tools is not null && queue.Settings.Capabilities is null)
            _ = DetectHardwareAsync();
    }

    // ------------------------------------------------------------------ form

    private void LoadForm(JobSettings s)
    {
        _loadingForm = true;
        SelectedProfile = s.ProfileDefinition;
        TenBit = s.TenBit;
        UseSidecar = s.UseSidecarSubtitles;
        SelectedQuality = QualityOptions.First(o => o.Value == s.Quality);
        SelectedResolution = ResolutionOptions.FirstOrDefault(o => o.Value == s.Resolution) ?? ResolutionOptions[0];
        SelectedSubtitles = SubtitleOptions.First(o => o.Value == s.Subtitles);
        _loadingForm = false;
        RebuildCodecs(s.Codec);
        RebuildBackends(s.Backend);
    }

    public JobSettings CurrentSettings() => new()
    {
        Profile = SelectedProfile.Kind,
        Codec = SelectedCodec?.Value ?? VideoCodec.H264,
        Backend = SelectedBackend?.Value ?? EncoderBackend.Software,
        Quality = SelectedQuality?.Value ?? QualityLevel.Normal,
        Resolution = SelectedResolution?.Value ?? 1080,
        Subtitles = SelectedSubtitles?.Value ?? SubtitleMode.Hardsub,
        TenBit = TenBit,
        UseSidecarSubtitles = UseSidecar,
    };

    partial void OnSelectedProfileChanged(ProfileDefinition value)
    {
        foreach (var n in new[] { nameof(ShowCodec), nameof(ShowEncoder), nameof(ShowQuality), nameof(ShowResolution), nameof(ShowSubtitles), nameof(ShowSidecar), nameof(ShowTenBit) })
            OnPropertyChanged(n);
        if (_loadingForm) return;
        RebuildCodecs(SelectedCodec?.Value);
        RebuildBackends(SelectedBackend?.Value);
    }

    partial void OnSelectedCodecChanged(Option<VideoCodec>? value)
    {
        if (!_loadingForm) RebuildBackends(SelectedBackend?.Value);
    }

    partial void OnTenBitChanged(bool value)
    {
        if (!_loadingForm) RebuildBackends(SelectedBackend?.Value);
    }

    partial void OnSelectedSubtitlesChanged(Option<SubtitleMode>? value) => OnPropertyChanged(nameof(ShowSidecar));

    partial void OnSelectedBackendChanged(BackendOption? value) =>
        BackendNote = !SelectedProfile.AllowsHardware ? "This profile is CPU only."
            : value?.Value == EncoderBackend.Software ? "CPU encoding: slowest, best compression."
            : "Hardware encoding: much faster, slightly bigger files.";

    private void RebuildCodecs(VideoCodec? keep)
    {
        var codecs = SelectedProfile.Codecs.Select(c => new Option<VideoCodec>(c, c.Display())).ToList();
        _loadingForm = true;
        CodecOptions = codecs;
        SelectedCodec = codecs.FirstOrDefault(c => c.Value == keep) ?? codecs.FirstOrDefault();
        _loadingForm = false;
    }

    private void RebuildBackends(EncoderBackend? keep)
    {
        var p = SelectedProfile;
        var codec = p.Kind == ProfileKind.RemuxMp4 ? VideoCodec.H264 : SelectedCodec?.Value ?? VideoCodec.H264;
        TenBitPossible = VideoEncoders.Supports10Bit(codec, EncoderBackend.Software) && p.Kind is ProfileKind.UniversalMkv or ProfileKind.Streaming;
        bool tenBit = TenBit && TenBitPossible;
        var caps = Queue.Settings.Capabilities;
        var list = new List<BackendOption>();
        foreach (var b in VideoEncoders.AllBackends)
        {
            if (!p.AllowsHardware && b != EncoderBackend.Software) continue;
            if (VideoEncoders.EncoderName(codec, b) is null) continue;
            bool ok = caps is null || caps.IsUsable(codec, b, tenBit);
            string note = ok ? "" : caps!.Find(codec, b) is { Compiled: false } ? " — not in this FFmpeg" : tenBit && caps.IsUsable(codec, b, false) ? " — 8-bit only" : " — not available";
            list.Add(new BackendOption(b, b.Display() + note, ok));
        }
        _loadingForm = true;
        BackendOptions = list;
        SelectedBackend = list.FirstOrDefault(o => o.Value == keep && o.IsEnabled) ?? list.FirstOrDefault(o => o.IsEnabled) ?? list.FirstOrDefault();
        _loadingForm = false;
        OnSelectedBackendChanged(SelectedBackend);
    }

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

    public void AddPaths(IEnumerable<string> paths) => Queue.AddPaths(paths, CurrentSettings());

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
            await (Dialogs?.ShowTextAsync("FFmpeg not found",
                "TnA needs FFmpeg.\n\n" + FfmpegDownloader.ManualInstructions + "\n\nOpen Settings to download it (Windows) or set its path.") ?? Task.CompletedTask);
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
        await Dialogs.ShowTextAsync("Log — " + j.FileName, string.IsNullOrWhiteSpace(log) ? "(empty — the log fills while the file is processed)" : log);
    }

    public async Task ShowCommandAsync(JobViewModel j)
    {
        if (Dialogs is null) return;
        string cmd;
        try { cmd = j.Job.CommandLine ?? await Queue.PreviewCommandAsync(j.Job); }
        catch (Exception ex) { cmd = "Cannot build the command: " + ex.Message; }
        await Dialogs.ShowTextAsync("FFmpeg command", cmd);
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
        QueueStatus = Queue.IsPaused ? "Paused"
            : Queue.IsRunning ? $"Encoding {done + 1}/{all.Count}{(cur is null ? "" : $" · {cur.Percent:0}%")}"
            : all.Count > 0 ? $"{ready} ready · {done} done{(failed > 0 ? $" · {failed} failed" : "")}" : "Idle";
        WindowTitle = Queue.IsRunning && cur is not null ? $"{cur.Percent:0}% · TnA" : "TnA - Tanoshimi no Autohardsubber";
    }

    // ------------------------------------------------------------------ hardware / settings

    [RelayCommand]
    private async Task DetectHardwareAsync()
    {
        IsDetecting = true;
        HardwareInfo = "Testing encoders…";
        try
        {
            var progress = new Progress<string>(l => HardwareInfo = l.Trim());
            await Task.Run(() => Queue.DetectHardwareAsync(progress));
        }
        catch (Exception ex)
        {
            HardwareInfo = "Detection failed: " + ex.Message;
        }
        finally
        {
            IsDetecting = false;
            RefreshHardware();
            RebuildBackends(SelectedBackend?.Value);
        }
    }

    private void RefreshHardware()
    {
        Hardware.Clear();
        var caps = Queue.Settings.Capabilities;
        if (caps is null)
        {
            HardwareInfo = Queue.Tools is null ? "FFmpeg not found — open Settings." : "Not detected yet.";
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
        HardwareInfo = $"{caps.Version.Replace("ffmpeg version ", "FFmpeg ").Split(" Copyright")[0]} · libass {(caps.HasLibass ? "✓" : "missing!")}";
    }

    [RelayCommand]
    private async Task OpenSettings()
    {
        if (Dialogs is null) return;
        await Dialogs.ShowSettingsAsync(this);
        Queue.RefreshTools();
        RefreshHardware();
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
