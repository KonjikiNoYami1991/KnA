using System.Text.Json;
using System.Text.RegularExpressions;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Ocr;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;

namespace KnA.Core.Remote;

/// <summary>
/// The desktop editor, wizard, AI assistant and OCR editor on a KnA server: the same calls as on this computer, through
/// the server's API (the same one the web UI uses; the analysis travels whole, the types are shared). Previews are
/// downloaded into a cache folder and shown like local ones.
/// </summary>
public sealed class RemoteEditorEngine(KnaClient client, string cacheDirectory) : IEditorEngine
{
    private sealed record EngineState(FfmpegTools? Tools, FfmpegCapabilities? Capabilities, List<GpuBenchmarkResult>? GpuBenchmarks,
        bool AiRuntime, Ai.AiSettings? Ai, OcrOptions? Ocr);
    private sealed record Materialized(JobSettings Settings, JobEdit Edit, JobAnalysis Full);
    private sealed record OptionsAnswer(string Program, List<ToolOption> Options);

    private EngineState _state = new(null, null, null, false, null, null);
    private IReadOnlyList<Preset> _presets = PresetStore.BuiltIns;

    public KnaClient Client => client;
    public bool IsRemote => true;
    public FfmpegCapabilities? Capabilities => _state.Capabilities;
    public FfmpegTools? Tools => _state.Tools;
    public IReadOnlyList<GpuBenchmarkResult> GpuBenchmarks => _state.GpuBenchmarks ?? [];
    public bool AiRuntimeInstalled => _state.AiRuntime;
    public Ai.AiSettings Ai => _state.Ai ?? new Ai.AiSettings();
    public OcrOptions OcrDefaults => _state.Ocr ?? new OcrOptions();

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _state = await client.GetAsync<EngineState>("api/engine", ct);
        await RefreshPresetsAsync(ct);
    }

    private async Task RefreshPresetsAsync(CancellationToken ct) => _presets = await client.GetAsync<List<Preset>>("api/presets", ct);

    // ------------------------------------------------------------------ presets (the server's: shared and the account's own)

    public IReadOnlyList<Preset> Presets => _presets;
    public Preset? GetPreset(string name) => _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public async Task<Preset> SavePresetAsync(Preset preset, CancellationToken ct = default)
    {
        var saved = await client.SendJsonAsync<Preset>(HttpMethod.Post, "api/presets", preset with { Scope = null }, ct);
        await RefreshPresetsAsync(ct);
        return saved;
    }

    public async Task<bool> DeletePresetAsync(string name, CancellationToken ct = default)
    {
        try { await client.PostAsync("api/presets/" + Uri.EscapeDataString(name), null, ct, HttpMethod.Delete); }
        catch (RemoteException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { return false; }
        await RefreshPresetsAsync(ct);
        return true;
    }

    // ------------------------------------------------------------------ the job in the editor

    /// <summary>The server's media analysis kept in the mirror job (size planner, AI assistant, chapters).</summary>
    private static JobAnalysis Keep(QueueJob job, JobAnalysis a)
    {
        job.Media = a.Source;
        foreach (var (k, v) in a.External) job.ExternalMedia[k] = v;
        return a;
    }

    public async Task<(JobSettings Settings, JobEdit Edit, JobAnalysis Analysis)> MaterializeAsync(QueueJob job, JobSettings settings, JobEdit edit, CancellationToken ct = default)
    {
        var m = await client.SendJsonAsync<Materialized>(HttpMethod.Post, $"api/jobs/{job.Id}/materialize?full=true", new { settings, edit }, ct);
        job.Settings = m.Settings;
        job.Edit = m.Edit;
        return (m.Settings, m.Edit, Keep(job, m.Full));
    }

    public async Task<JobAnalysis> SetEditAsync(QueueJob job, JobEdit edit, JobSettings settings, CancellationToken ct = default)
    {
        var a = await client.SendJsonAsync<JobAnalysis>(HttpMethod.Put, $"api/jobs/{job.Id}/edit?full=true", new { settings, edit }, ct);
        job.Settings = settings;
        job.Edit = edit;
        return Keep(job, a);
    }

    public async Task<JobAnalysis> AnalyzeAsync(QueueJob job, CancellationToken ct = default) =>
        Keep(job, await client.GetAsync<JobAnalysis>($"api/jobs/{job.Id}/analysis?full=true", ct));

    public async Task<(string Program, IReadOnlyList<ToolOption> Options)> ParameterOptionsAsync(string tool, JobSettings settings, CancellationToken ct = default)
    {
        var r = await client.SendJsonAsync<OptionsAnswer>(HttpMethod.Post, "api/tools/options", new { tool, settings }, ct);
        return (r.Program, r.Options);
    }

    public EncoderOverrides ExplicitRate(JobSettings settings, VideoStream? video, string container) =>
        EditorModel.ExplicitRate(Capabilities, settings, video, container);

    /// <summary>Worked out here from the server's analysis and hardware (the same planner it uses).</summary>
    public SizeBudget? SizeBudget(QueueJob job, JobSettings settings, JobEdit edit)
    {
        if (job.Media is null) return null;
        var ctx = JobRunner.BuildContext(job, new AppSettings { Capabilities = Capabilities }, Tools) with { Settings = settings, Edit = edit };
        return SizePlanner.For(ctx);
    }

    public async Task<SizeEstimate> EstimateSizeAsync(QueueJob job, JobSettings settings, JobEdit edit, CancellationToken ct = default)
    {
        var r = await client.SendJsonAsync<JsonElement>(HttpMethod.Post, $"api/jobs/{job.Id}/estimate", new { settings, edit }, ct);
        if (r.TryGetProperty("estimate", out var e) && e.ValueKind == JsonValueKind.Object) return e.Deserialize<SizeEstimate>(SettingsStore.Json)!;
        throw new InvalidOperationException(r.TryGetProperty("text", out var t) ? t.GetString() : L.T("The server could not estimate the size."));
    }

    public Task<string> CommandAsync(QueueJob job, CancellationToken ct = default) => client.CommandAsync(job.Id, ct);

    public async Task<IReadOnlyList<string>> ExternalFilesAsync(IReadOnlyList<string> localPaths, IProgress<string>? status = null, CancellationToken ct = default)
    {
        var result = new List<string>();
        foreach (var p in localPaths)
        {
            status?.Report(L.T("Uploading {0} to the server…", Path.GetFileName(p)));
            result.Add(await client.UploadFileAsync(p, ct));
        }
        return result;
    }

    public async Task<IReadOnlyList<Guid>> SplitSegmentsAsync(QueueJob job, CancellationToken ct = default) =>
        await client.SendJsonAsync<List<Guid>>(HttpMethod.Post, $"api/jobs/{job.Id}/split-segments", null, ct);

    public async Task RunGpuBenchmarkAsync(EncoderBackend backend, VideoCodec codec, CancellationToken ct = default)
    {
        await client.PostAsync("api/gpu/benchmark", new { backend, codec }, ct);
        await RefreshAsync(ct);
    }

    public async Task InstallComponentAsync(string id, IProgress<double>? progress = null, IProgress<string>? log = null, CancellationToken ct = default)
    {
        log?.Report(L.T("Installing {0} on the KnA server…", id));
        await client.PostAsync($"api/components/{Uri.EscapeDataString(id)}/install", null, ct);
        await RefreshAsync(ct);
    }

    // ------------------------------------------------------------------ previews

    private string Cache(Guid job, string name) => Path.Combine(cacheDirectory, job.ToString("N"), name);
    private static string N(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // A final frame depends on the settings of the moment: always asked again (the server keeps its own cache).
    public Task<string> FrameAsync(Guid job, double seconds, bool final, int width, CancellationToken ct = default) =>
        client.SaveToFileAsync($"api/jobs/{job}/preview/frame?t={N(seconds)}&final={(final ? "true" : "false")}&w={width}",
            Cache(job, $"frame-{Guid.NewGuid():N}.png"), ct);

    public async Task<string> ThumbnailAsync(Guid job, double seconds, int width, CancellationToken ct = default)
    {
        var file = Cache(job, $"thumb-{N(seconds)}-{width}.jpg");
        return File.Exists(file) ? file : await client.SaveToFileAsync($"api/jobs/{job}/preview/thumb?t={N(seconds)}&w={width}", file, ct);
    }

    public async Task<string> WaveformAsync(Guid job, string file, int stream, int width, int height, CancellationToken ct = default)
    {
        var local = Cache(job, $"wave-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(file)))[..12]}-{stream}-{width}x{height}.png");
        return File.Exists(local) ? local
            : await client.SaveToFileAsync($"api/jobs/{job}/preview/waveform?file={Uri.EscapeDataString(file)}&stream={stream}&w={width}&h={height}", local, ct);
    }

    public Task<string> ClipAsync(Guid job, double seconds, double length, CancellationToken ct = default) =>
        client.SaveToFileAsync($"api/jobs/{job}/preview/clip?t={N(seconds)}&len={N(length)}", Cache(job, $"clip-{Guid.NewGuid():N}.mp4"), ct);

    public async Task<CropSettings?> DetectCropAsync(Guid job, CancellationToken ct = default)
    {
        var r = await client.GetAsync<JsonElement>($"api/jobs/{job}/cropdetect", ct);
        return r.ValueKind == JsonValueKind.Object ? r.Deserialize<CropSettings>(SettingsStore.Json) : null;
    }

    /// <summary>The preview files of the server's jobs are only a cache: removed when the connection ends.</summary>
    public void ClearCache()
    {
        try { if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ OCR and AI (they run on the server)

    public async Task<IOcrSession> OpenOcrAsync(QueueJob job, string file, int stream, int copy, CancellationToken ct = default) =>
        await RemoteOcrSession.OpenAsync(client, job, file, stream, copy, ct);

    public Task<Ai.AiSuggestion> SuggestAsync(string prompt, JobSettings current, QueueJob? job, CancellationToken ct = default) =>
        client.SendJsonAsync<Ai.AiSuggestion>(HttpMethod.Post, "api/ai/suggest", new { prompt, settings = current, jobId = job?.Id }, ct);

    public Task<Ai.AiSuggestion> AnalyzeFramesAsync(QueueJob job, JobSettings current, CancellationToken ct = default) =>
        client.SendJsonAsync<Ai.AiSuggestion>(HttpMethod.Post, $"api/jobs/{job.Id}/ai/analyze", null, ct);
}

/// <summary>
/// The OCR editor on a KnA server: the pictures come once, the reading runs on the server (progress polled), every
/// correction is sent as it is made; "Save as" writes the SRT here, "Use in the job" there.
/// </summary>
public sealed class RemoteOcrSession : IOcrSession
{
    private sealed record Opened(Guid Id, int Count, string? Language, string? Codec);
    private sealed record LineDto(int Index, double Start, double End, string Text, double Confidence, bool Italic, bool Forced, bool Edited);
    private sealed record StateDto(bool Busy, int Done, List<LineDto> Lines);

    private readonly KnaClient _client;
    private readonly Guid _id;
    private readonly SemaphoreSlim _send = new(1);
    private Task _pending = Task.CompletedTask;

    public QueueJob Job { get; }
    public string File { get; }
    public int Stream { get; }
    public string? Language { get; }
    public List<OcrLine> Lines { get; } = [];
    public bool Busy { get; private set; }

    private RemoteOcrSession(KnaClient client, Guid id, QueueJob job, string file, int stream, string? language)
    {
        _client = client; _id = id; Job = job; File = file; Stream = stream; Language = language;
    }

    public static async Task<RemoteOcrSession> OpenAsync(KnaClient client, QueueJob job, string file, int stream, int copy, CancellationToken ct)
    {
        var o = await client.SendJsonAsync<Opened>(HttpMethod.Post, "api/ocr/sessions", new { jobId = job.Id, file, stream, copy }, ct);
        var s = new RemoteOcrSession(client, o.Id, job, file, stream, o.Language);
        var st = await client.GetAsync<StateDto>($"api/ocr/{o.Id}", ct);
        await using var data = await client.OpenStreamAsync($"api/ocr/{o.Id}/images", ct);
        var head = new byte[12];
        foreach (var l in st.Lines)
        {
            await data.ReadExactlyAsync(head, ct);
            int w = BitConverter.ToInt32(head, 0), h = BitConverter.ToInt32(head, 4), n = BitConverter.ToInt32(head, 8);
            var rgba = new byte[n];
            await data.ReadExactlyAsync(rgba, ct);
            var line = new OcrLine(l.Index, new SubtitleImage(TimeSpan.FromSeconds(l.Start), TimeSpan.FromSeconds(l.End), w, h, rgba) { Forced = l.Forced });
            s.Lines.Add(line);
        }
        s.Apply(st);
        return s;
    }

    private void Apply(StateDto st)
    {
        Busy = st.Busy;
        foreach (var l in st.Lines)
            if (l.Index >= 0 && l.Index < Lines.Count)
            {
                var x = Lines[l.Index];
                x.Text = l.Text; x.Confidence = l.Confidence; x.Italic = l.Italic; x.Edited = l.Edited;
                x.Start = TimeSpan.FromSeconds(l.Start); x.End = TimeSpan.FromSeconds(l.End);
            }
    }

    public bool ToCheck(OcrLine l) => l.Confidence >= 0 && (l.Text.Trim().Length == 0 || l.Confidence < OcrSession.CheckBelow);

    /// <summary>Corrections go to the server one after the other, in the order they were made.</summary>
    private void Send(OcrLine l)
    {
        var body = new { text = l.Text, italic = l.Italic, start = l.Start.TotalSeconds, end = l.End.TotalSeconds };
        var index = l.Index;
        lock (_send)
            _pending = _pending.ContinueWith(_ => _client.PostAsync($"api/ocr/{_id}/lines/{index}", body, CancellationToken.None, HttpMethod.Put)).Unwrap();
    }

    private async Task FlushAsync()
    {
        Task t;
        lock (_send) t = _pending;
        try { await t; }
        catch (Exception ex) when (ex is RemoteException or HttpRequestException) { throw new RemoteException(L.T("A correction did not reach the server: {0}", ex.Message)); }
    }

    public void Edit(int index, string? text = null, bool? italic = null, TimeSpan? start = null, TimeSpan? end = null)
    {
        if (index < 0 || index >= Lines.Count) return;
        var l = Lines[index];
        if (text is not null) l.Text = text;
        if (italic is { } i) l.Italic = i;
        if (start is { } a) l.Start = a;
        if (end is { } b) l.End = b;
        l.Edited = true;
        Send(l);
    }

    public int Replace(string find, string replace, bool regex, bool matchCase)
    {
        if (string.IsNullOrEmpty(find)) return 0;
        var rx = regex ? new Regex(find, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)) : null;
        int n = 0;
        foreach (var l in Lines)
        {
            var t = rx is not null ? rx.Replace(l.Text, replace) : l.Text.Replace(find, replace, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (t == l.Text) continue;
            l.Text = t; l.Edited = true; n++;
            Send(l);
        }
        return n;
    }

    public async Task RecognizeAsync(OcrOptions options, IReadOnlyCollection<int>? only, IProgress<int>? progress, CancellationToken ct)
    {
        await FlushAsync();
        await _client.PostAsync($"api/ocr/{_id}/run", new { options, only = only?.ToList() }, ct);
        Busy = true;
        while (true)
        {
            await Task.Delay(700, ct);
            var st = await _client.GetAsync<StateDto>($"api/ocr/{_id}", ct);
            progress?.Report(st.Done);
            if (!st.Busy) { Apply(st); return; }
        }
    }

    public string DefaultSrtPath() =>
        Path.ChangeExtension(Job.SourcePath.Replace('\\', '/').Split('/').Last(), null) + "." + (Language ?? "und") + ".srt";

    public async Task<int> SaveAsync(string srtPath, CancellationToken ct = default)
    {
        await FlushAsync();
        return await SubtitleOcr.WriteSrtAsync(Lines, srtPath, ct);
    }

    public async Task UseInJobAsync(CancellationToken ct = default)
    {
        await FlushAsync();
        await _client.PostAsync($"api/ocr/{_id}/save?use=true", null, ct);
    }

    // The server remembers the options of an administrator's run by itself.
    public void RememberOptions(OcrOptions options) { }
}
