using KnA.Core.Encoding;
using KnA.Core.Media;
using KnA.Core.Ocr;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.Core.Queue;

/// <summary>
/// What the desktop editor, the preset wizard, the AI assistant and the OCR editor need from the machine that encodes:
/// this computer (<see cref="LocalEditorEngine"/>) or a KnA server (<see cref="Remote.RemoteEditorEngine"/>, through
/// the same API as the web UI). The windows are the same in both cases.
/// </summary>
public interface IEditorEngine
{
    bool IsRemote { get; }
    /// <summary>The hardware test of the machine that encodes.</summary>
    FfmpegCapabilities? Capabilities { get; }
    /// <summary>Its programs (only whether each one is there matters to the windows).</summary>
    FfmpegTools? Tools { get; }
    IReadOnlyList<GpuBenchmarkResult> GpuBenchmarks { get; }
    bool AiRuntimeInstalled { get; }
    Ai.AiSettings Ai { get; }
    OcrOptions OcrDefaults { get; }

    /// <summary>Reads the machine's state again (a server: tools, hardware, presets).</summary>
    Task RefreshAsync(CancellationToken ct = default);

    // presets
    IReadOnlyList<Preset> Presets { get; }
    Preset? GetPreset(string name);
    Task<Preset> SavePresetAsync(Preset preset, CancellationToken ct = default);
    Task<bool> DeletePresetAsync(string name, CancellationToken ct = default);

    // a job in the editor
    Task<(JobSettings Settings, JobEdit Edit, JobAnalysis Analysis)> MaterializeAsync(QueueJob job, JobSettings settings, JobEdit edit, CancellationToken ct = default);
    Task<JobAnalysis> SetEditAsync(QueueJob job, JobEdit edit, JobSettings settings, CancellationToken ct = default);
    Task<JobAnalysis> AnalyzeAsync(QueueJob job, CancellationToken ct = default);
    Task<(string Program, IReadOnlyList<ToolOption> Options)> ParameterOptionsAsync(string tool, JobSettings settings, CancellationToken ct = default);
    EncoderOverrides ExplicitRate(JobSettings settings, VideoStream? video, string container);
    SizeBudget? SizeBudget(QueueJob job, JobSettings settings, JobEdit edit);
    Task<SizeEstimate> EstimateSizeAsync(QueueJob job, JobSettings settings, JobEdit edit, CancellationToken ct = default);
    Task<string> CommandAsync(QueueJob job, CancellationToken ct = default);
    /// <summary>
    /// Files of this computer the job will use (subtitles, audio, fonts, attachments): their paths as the machine that
    /// encodes sees them (a server gets them uploaded first).
    /// </summary>
    Task<IReadOnlyList<string>> ExternalFilesAsync(IReadOnlyList<string> localPaths, IProgress<string>? status = null, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> SplitSegmentsAsync(QueueJob job, CancellationToken ct = default);
    Task RunGpuBenchmarkAsync(EncoderBackend backend, VideoCodec codec, CancellationToken ct = default);
    Task InstallComponentAsync(string id, IProgress<double>? progress = null, IProgress<string>? log = null, CancellationToken ct = default);

    // previews: local files (a server's are downloaded into a cache)
    Task<string> FrameAsync(Guid job, double seconds, bool final, int width, CancellationToken ct = default);
    Task<string> ThumbnailAsync(Guid job, double seconds, int width, CancellationToken ct = default);
    Task<string> WaveformAsync(Guid job, string file, int stream, int width, int height, CancellationToken ct = default);
    Task<string> ClipAsync(Guid job, double seconds, double length, CancellationToken ct = default);
    Task<CropSettings?> DetectCropAsync(Guid job, CancellationToken ct = default);

    // OCR editor and AI assistant
    Task<IOcrSession> OpenOcrAsync(QueueJob job, string file, int stream, int copy, CancellationToken ct = default);
    Task<Ai.AiSuggestion> SuggestAsync(string prompt, JobSettings current, QueueJob? job, CancellationToken ct = default);
    Task<Ai.AiSuggestion> AnalyzeFramesAsync(QueueJob job, JobSettings current, CancellationToken ct = default);
}

/// <summary>This computer: <see cref="QueueService"/> and its helpers, as before.</summary>
public sealed class LocalEditorEngine(QueueService queue) : IEditorEngine
{
    public QueueService Queue => queue;
    public bool IsRemote => false;
    public FfmpegCapabilities? Capabilities => queue.Settings.Capabilities;
    public FfmpegTools? Tools => queue.Tools;
    public IReadOnlyList<GpuBenchmarkResult> GpuBenchmarks => queue.Settings.GpuBenchmarks;
    public bool AiRuntimeInstalled => File.Exists(ComponentManager.VenvPython(queue.Store.DataDirectory));
    public Ai.AiSettings Ai => queue.Settings.Ai;
    public OcrOptions OcrDefaults => queue.Settings.Ocr ?? new OcrOptions();
    public Task RefreshAsync(CancellationToken ct = default) { queue.RefreshTools(); return Task.CompletedTask; }

    public IReadOnlyList<Preset> Presets => queue.Presets.All;
    public Preset? GetPreset(string name) => queue.Presets.Get(name);
    public Task<Preset> SavePresetAsync(Preset preset, CancellationToken ct = default) => Task.FromResult(queue.Presets.Save(preset));
    public Task<bool> DeletePresetAsync(string name, CancellationToken ct = default) => Task.FromResult(queue.Presets.Delete(name));

    public Task<(JobSettings Settings, JobEdit Edit, JobAnalysis Analysis)> MaterializeAsync(QueueJob job, JobSettings settings, JobEdit edit, CancellationToken ct = default) =>
        EditorModel.MaterializeAsync(queue, job.Id, settings, edit, ct);
    public Task<JobAnalysis> SetEditAsync(QueueJob job, JobEdit edit, JobSettings settings, CancellationToken ct = default) => queue.SetEditAsync(job.Id, edit, ct, settings);
    public Task<JobAnalysis> AnalyzeAsync(QueueJob job, CancellationToken ct = default) => queue.AnalyzeAsync(job, ct);
    public Task<(string Program, IReadOnlyList<ToolOption> Options)> ParameterOptionsAsync(string tool, JobSettings settings, CancellationToken ct = default) =>
        queue.ParameterOptionsAsync(tool, settings, ct);
    public EncoderOverrides ExplicitRate(JobSettings settings, VideoStream? video, string container) => EditorModel.ExplicitRate(queue, settings, video, container);
    public SizeBudget? SizeBudget(QueueJob job, JobSettings settings, JobEdit edit) => queue.SizeBudget(job, settings, edit);
    public Task<SizeEstimate> EstimateSizeAsync(QueueJob job, JobSettings settings, JobEdit edit, CancellationToken ct = default) =>
        SizeEstimator.EstimateAsync(queue, job, settings, edit, ct: ct);
    public async Task<string> CommandAsync(QueueJob job, CancellationToken ct = default) => job.CommandLine ?? await queue.PreviewCommandAsync(job);
    public Task<IReadOnlyList<string>> ExternalFilesAsync(IReadOnlyList<string> localPaths, IProgress<string>? status = null, CancellationToken ct = default) =>
        Task.FromResult(localPaths);
    public Task<IReadOnlyList<Guid>> SplitSegmentsAsync(QueueJob job, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(queue.SplitSegments(job.Id).Select(j => j.Id).ToList());
    public Task RunGpuBenchmarkAsync(EncoderBackend backend, VideoCodec codec, CancellationToken ct = default) => GpuBenchmark.RunAsync(queue, backend, codec, null, ct);
    public Task InstallComponentAsync(string id, IProgress<double>? progress = null, IProgress<string>? log = null, CancellationToken ct = default) =>
        queue.Components.InstallAsync(id, progress, log, ct);

    public Task<string> FrameAsync(Guid job, double seconds, bool final, int width, CancellationToken ct = default) => queue.Preview.FrameAsync(job, seconds, final, width, ct);
    public Task<string> ThumbnailAsync(Guid job, double seconds, int width, CancellationToken ct = default) => queue.Preview.ThumbnailAsync(job, seconds, width, ct);
    public Task<string> WaveformAsync(Guid job, string file, int stream, int width, int height, CancellationToken ct = default) =>
        queue.Preview.WaveformAsync(job, file, stream, width, height, ct);
    public Task<string> ClipAsync(Guid job, double seconds, double length, CancellationToken ct = default) => queue.Preview.ClipAsync(job, seconds, length, ct);
    public Task<CropSettings?> DetectCropAsync(Guid job, CancellationToken ct = default) => queue.Preview.DetectCropAsync(job, ct);

    public async Task<IOcrSession> OpenOcrAsync(QueueJob job, string file, int stream, int copy, CancellationToken ct = default) =>
        new LocalOcrSession(queue, await OcrSession.OpenAsync(queue, job, file, stream, ct, copy));

    public Task<Ai.AiSuggestion> SuggestAsync(string prompt, JobSettings current, QueueJob? job, CancellationToken ct = default) =>
        new Ai.Assistant(queue.Settings.Ai, queue.Settings.Capabilities).SuggestAsync(prompt, current, job?.Media, ct);

    public async Task<Ai.AiSuggestion> AnalyzeFramesAsync(QueueJob job, JobSettings current, CancellationToken ct = default)
    {
        var a = await queue.AnalyzeAsync(job, ct);
        var frames = new List<byte[]>();
        foreach (var f in new[] { 0.12, 0.37, 0.62, 0.87 })
            frames.Add(await File.ReadAllBytesAsync(await queue.Preview.ThumbnailAsync(job.Id, a.Source.Duration.TotalSeconds * f, 768, ct), ct));
        return await new Ai.Assistant(queue.Settings.Ai, queue.Settings.Capabilities).AnalyzeAsync(a.Source, current, frames, ct);
    }
}
