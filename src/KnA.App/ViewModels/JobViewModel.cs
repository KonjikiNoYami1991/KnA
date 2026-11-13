using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KnA.Core.Encoding;
using KnA.Core.Queue;

namespace KnA.App.ViewModels;

public sealed partial class JobViewModel(QueueJob job, MainViewModel owner) : ObservableObject
{
    public QueueJob Job { get; } = job;
    public Guid Id => Job.Id;
    public string FileName => Job.FileName;
    public string Folder => Job.Folder;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _media = KnA.Core.Localization.L.T("Analysing…");
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private IReadOnlyList<Chip> _chips = [];
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _canEdit = true;

    partial void OnIsSelectedChanged(bool value) => owner.OnSelectionChanged();

    public void Refresh()
    {
        var j = Job;
        Status = KnA.Core.Localization.L.T(j.Status.ToString()).ToUpperInvariant();
        IsRunning = j.Status == JobStatus.Running;
        IsPaused = j.Status == JobStatus.Paused;
        IsDone = j.Status is JobStatus.Done or JobStatus.Warning;
        IsError = j.Status is JobStatus.Error or JobStatus.Stopped;
        CanEdit = !(IsRunning || IsPaused);
        Percent = j.Percent;
        ShowProgress = IsRunning || IsPaused || j.Percent > 0;
        Message = j.Message;
        Media = j.Media?.Summary ?? j.ProbeError ?? KnA.Core.Localization.L.T("Analysing…");
        Chips = BuildChips(j.Settings, j.Edit);

        if (IsRunning || IsPaused)
        {
            var p = j.Progress;
            Stats = p is null
                ? j.Stage ?? ""
                : $"{j.Stage}   {Fmt(p.Position)} / {Fmt(p.Duration)}   {p.Fps:0} fps   {p.Speed:0.00}×   ETA {Fmt(p.Remaining)}   {Size(p.SizeBytes)}{(p.EstimatedFinalSize is { } e ? " → ~" + Size(e) : "")}";
        }
        else if (j.Elapsed is { } took)
        {
            Stats = $"{KnA.Core.Localization.L.T("took")} {Fmt(took)}" + (j.OutputPath is { } o ? "   → " + Path.GetFileName(o) : "");
        }
        else Stats = "";
    }

    private static IReadOnlyList<Chip> BuildChips(JobSettings s, JobEdit e)
    {
        var p = s.ProfileDefinition;
        var chips = new List<Chip> { new(s.PresetName ?? p.Name) };
        if (p.AllowsCodecChoice) chips.Add(new(s.EffectiveCodec.Short()));
        if (p.EncodesVideo && s.EffectiveBackend != EncoderBackend.Software) chips.Add(new(s.EffectiveBackend.Display(), "hw"));
        if (s.EffectiveTenBit) chips.Add(new("10-bit", "bit"));
        if (p.UsesResolution) chips.Add(new(Resolutions.Display(s.Resolution)));
        if (p.UsesQuality) chips.Add(new(s.Quality.Display()));
        if (p.UsesSubtitleMode) chips.Add(new(s.Subtitles.Display()));
        if (p.EncodesVideo && s.Chunks.Enabled) chips.Add(new(KnA.Core.Localization.L.T("Chunked ×{0}", s.Chunks.Workers), "bit"));
        if (s.Frameserver.Kind != KnA.Core.Media.FrameserverKind.None) chips.Add(new(s.Frameserver.Kind.ToString(), "hw"));
        if (e.Tracks is not null || e.ExternalFiles.Count > 0) chips.Add(new("✎ " + KnA.Core.Localization.L.T("edited")));
        if (e.IsTrimmed) chips.Add(new($"✂ {(e.TrimStart ?? 0):0.#}s–{(e.TrimEnd is { } te ? te.ToString("0.#") + "s" : KnA.Core.Localization.L.T("end"))}"));
        return chips;
    }

    private static string Fmt(TimeSpan? t) => t is { } v ? v.ToString(v.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss") : "–";

    private static string Size(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    [RelayCommand] private Task Edit() => owner.OpenEditorAsync(this);
    [RelayCommand] private void Remove() => owner.Queue.Remove([Id]);
    [RelayCommand] private void Retry() => owner.Queue.Reset([Id]);
    [RelayCommand] private void MoveUp() => owner.Queue.Move(Id, -1);
    [RelayCommand] private void MoveDown() => owner.Queue.Move(Id, 1);
    [RelayCommand] private Task ShowLog() => owner.ShowLogAsync(this);
    [RelayCommand] private Task ShowCommand() => owner.ShowCommandAsync(this);
}
