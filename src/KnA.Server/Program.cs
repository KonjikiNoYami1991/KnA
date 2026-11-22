using System.Diagnostics;
using System.Globalization;
using System.Text;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Queue;
using KnA.Core.Settings;
using KnA.Core.Tools;
using KnA.Server;

// Release packaging: KnA.Server --bundle <portable folder> ffmpeg,nvencc,model:realesr-animevideov3,…
if (args is ["--bundle", var bundleRoot, var bundleIds])
    return await ComponentManager.BundleAsync(Path.GetFullPath(bundleRoot),
        bundleIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Console.Out);

// Headless encoding (scripts, scheduled tasks): KnA-server --encode <files> [--preset …] — see --help-cli.
if (KnA.Core.Queue.CommandLine.Handles(args))
{
    using var cliCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cliCts.Cancel(); };
    return await KnA.Core.Queue.CommandLine.RunAsync(args, Console.Out, cliCts.Token);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Serve the UI from next to the executable, whatever the current directory is.
    ContentRootPath = AppContext.BaseDirectory,
});

// Windows service (deploy/install-windows-server.ps1): start/stop from the Service Control Manager, events in the
// Windows log. Does nothing when started from a console.
builder.Host.UseWindowsService(o => o.ServiceName = "KnA");

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) && !args.Any(a => a.StartsWith("--urls")))
    builder.WebHost.UseUrls(AppEnv.Get("URLS") ?? "http://0.0.0.0:8585");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 4 << 20);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = SettingsStore.Json.PropertyNamingPolicy;
    foreach (var c in SettingsStore.Json.Converters) o.SerializerOptions.Converters.Add(c);
});
var store = new SettingsStore();
var uploadsService = new UploadService(store);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(uploadsService);
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton(_ => new FileBrowser(AppEnv.Get("MEDIA_ROOTS"), AppEnv.Get("READONLY_ROOTS"), [uploadsService.DefaultFolder]));
builder.Services.AddSingleton<WatchFolderService>();

var app = builder.Build();
var queue = app.Services.GetRequiredService<QueueService>();
AutoUpdateResult autoUpdate = new(AutoUpdateState.UpToDate, "", new UpdateCheck(AppUpdater.CurrentVersion, null, false, null, ""));
var hub = app.Services.GetRequiredService<EventHub>();
var browser = app.Services.GetRequiredService<FileBrowser>();
var uploads = app.Services.GetRequiredService<UploadService>();
var watch = app.Services.GetRequiredService<WatchFolderService>();
watch.StatusChanged += st => hub.Broadcast("watch", st);
watch.JobsQueued += jobs => hub.Broadcast("toast", new { text = L.T("Watch folder: {0} file(s) queued", jobs.Count) });
var log = app.Logger;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    log.LogCritical("Crash log: {Path}", queue.Store.WriteCrashLog((Exception)e.ExceptionObject, "server"));

bool advancedAllowed = Env("KNA_ALLOW_ADVANCED");
bool powerActionsAllowed = Env("KNA_ALLOW_POWER_ACTIONS");
bool componentsLocked = Env("KNA_LOCK_COMPONENTS");

// ---------------------------------------------------------------- optional password (HTTP Basic)
var password = AppEnv.Get("PASSWORD");
if (!string.IsNullOrEmpty(password))
{
    app.Use(async (ctx, next) =>
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var pass = decoded[(decoded.IndexOf(':') + 1)..];
                if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(pass), Encoding.UTF8.GetBytes(password)))
                {
                    await next();
                    return;
                }
            }
            catch (FormatException) { }
        }
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"KnA\", charset=\"UTF-8\"";
        ctx.Response.StatusCode = 401;
    });
}

// ---------------------------------------------------------------- language: Italian browsers get Italian
app.Use(async (ctx, next) =>
{
    var lang = ctx.Request.Query["lang"].ToString();
    if (string.IsNullOrEmpty(lang)) lang = ctx.Request.Headers.AcceptLanguage.ToString();
    var culture = lang.StartsWith("it", StringComparison.OrdinalIgnoreCase) ? new CultureInfo("it-IT") : new CultureInfo("en-US");
    CultureInfo.CurrentUICulture = culture;
    await next();
});

// ---------------------------------------------------------------- errors → JSON problem details
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or IOException or ArgumentException
                               && !ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex switch { KeyNotFoundException => 404, UnauthorizedAccessException => 403, _ => 400 };
        await ctx.Response.WriteAsJsonAsync(new { title = ex.Message, detail = ex.Message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------- state, i18n & events
app.MapGet("/api/state", () => new StateDto(AppInfo.Version, Platform(), L.Language, Mapping.PublicSettings(queue.Settings, SettingsStore.Json), OptionsDto.Create(),
    Mapping.Tools(queue), Mapping.QueueState(queue), queue.Jobs.Select(JobDto.From).ToList(), powerActionsAllowed, queue.Presets.All));

app.MapGet("/api/i18n", () => new { lang = L.Language, table = L.IsItalian() ? L.ItalianTable : new Dictionary<string, string>() });

app.MapGet("/api/events", (HttpContext ctx, CancellationToken ct) => hub.StreamAsync(ctx, ct));

app.MapGet("/api/frameserver/templates", () => KnA.Core.Media.Frameserver.Templates.Select(t => new { t.Name, Kind = t.Kind.ToString(), t.Body }));
app.MapGet("/api/info", () => new
{
    advancedAllowed, powerActionsAllowed, componentsLocked, roots = browser.Roots, writableRoots = browser.WritableRoots,
    uploadDir = uploads.DefaultFolder, maxUploadBytes = uploads.MaxFileBytes == long.MaxValue ? (long?)null : uploads.MaxFileBytes,
    vspipe = queue.Tools?.VsPipe, fdkaac = queue.Tools?.Fdkaac,
    dataDirectory = queue.Store.DataDirectory, version = AppInfo.Version, platform = Platform(),
});

// ---------------------------------------------------------------- files
app.MapGet("/api/browse", (string? path, bool? external, string? mode, bool? hidden) =>
{
    try { return Results.Ok(browser.Browse(path, mode ?? (external == true ? "external" : "video"), hidden ?? false)); }
    catch (UnauthorizedAccessException ex) { return Results.Problem(ex.Message, statusCode: 403); }
    catch (IOException ex) { return Results.Problem(ex.Message, statusCode: 400); }
});

// Downloads: a file (resumable, range requests) or a whole folder as a ZIP streamed on the fly.
app.MapGet("/api/files/download", (string path, HttpContext ctx, CancellationToken ct) => Download(path, ctx, ct));
app.MapGet("/api/jobs/{id:guid}/download", (Guid id, HttpContext ctx, CancellationToken ct) =>
    queue.Find(id)?.OutputPath is { } o ? Download(o, ctx, ct) : Task.FromResult(Results.NotFound()));

app.MapPost("/api/files/mkdir", (FolderRequest req) =>
{
    if (!browser.IsWritable(req.Path)) return Results.Problem(L.T("This folder is read-only for KnA."), statusCode: 403);
    Directory.CreateDirectory(req.Path);
    return Results.Ok(new { path = Path.GetFullPath(req.Path) });
});

// ---------------------------------------------------------------- uploads (chunked, resumable)
app.MapPost("/api/uploads", (UploadCreate req) =>
{
    var folder = string.IsNullOrWhiteSpace(req.Folder) ? uploads.DefaultFolder : req.Folder;
    if (!browser.IsWritable(folder)) return Results.Problem(L.T("This folder is read-only for KnA."), statusCode: 403);
    var u = uploads.Create(req.Name, req.Size, folder);
    return Results.Ok(new { u.Id, u.Offset, u.Name, chunk = 16 << 20 });
});
app.MapGet("/api/uploads/{id}", (string id) => uploads.Get(id) is { } u ? Results.Ok(new { u.Id, u.Offset, u.Size }) : Results.NotFound());
app.MapPut("/api/uploads/{id}", async (string id, long offset, HttpContext ctx, CancellationToken ct) =>
{
    if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f)
        f.MaxRequestBodySize = UploadService.MaxChunk;
    var (ok, current) = await uploads.AppendAsync(id, offset, ctx.Request.Body, ct);
    return ok ? Results.Ok(new { offset = current }) : Results.Json(new { offset = current }, statusCode: 409);
});
app.MapDelete("/api/uploads/{id}", (string id) => { uploads.Cancel(id); return Results.Ok(); });
app.MapPost("/api/uploads/{id}/complete", async (string id, UploadComplete req, CancellationToken ct) =>
{
    var path = uploads.Complete(id);
    int added = 0;
    var target = req.AttachTo is { } jid ? queue.Find(jid)
        : req.AttachToSource is { Length: > 0 } src ? queue.Jobs.LastOrDefault(j => j.SourcePath == src) : null;
    if (target is not null)
    {
        await queue.AttachExternalsAsync(target.Id, [path], ct);
        return Results.Ok(new { path, added = 0, attached = target.Id });
    }
    if (req.AttachTo is not null || req.AttachToSource is not null)
    {
        return Results.Ok(new { path, added = 0, attached = (Guid?)null });
    }
    else if (req.Enqueue && (MediaExtensions.IsVideo(path) || path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)))
    {
        var settings = req.Preset is { Length: > 0 } pn && queue.Presets.All.FirstOrDefault(p => p.Name == pn) is { } preset
            ? preset.Settings with { PresetName = preset.Name } : req.Settings ?? queue.Settings.Defaults;
        added = queue.AddPaths([path], settings).Count;
    }
    return Results.Ok(new { path, added });
});

// ---------------------------------------------------------------- smart drag & drop (server paths)
app.MapPost("/api/drop/classify", (DropRequest req) =>
{
    if (req.Paths.FirstOrDefault(p => !browser.IsAllowed(p)) is { } bad)
        return Results.Problem(L.T("Outside the allowed media roots: {0}", bad), statusCode: 403);
    return Results.Ok(DropClassifier.Classify(req.Paths, queue, req.TargetJob));
});
app.MapPost("/api/drop/apply", async (DropRequest req, CancellationToken ct) =>
{
    if (req.Paths.FirstOrDefault(p => !browser.IsAllowed(p)) is { } bad)
        return Results.Problem(L.T("Outside the allowed media roots: {0}", bad), statusCode: 403);
    var plan = DropClassifier.Classify(req.Paths, queue, req.TargetJob);
    var r = await DropClassifier.ApplyAsync(plan, queue, req.Settings ?? queue.Settings.Defaults, ct);
    if (r.Presets > 0) hub.Broadcast("presets", queue.Presets.All);
    return Results.Ok(r);
});

// ---------------------------------------------------------------- AI: LLM assistant and neural upscaler
app.MapGet("/api/ai/models", async (CancellationToken ct) =>
{
    try { return Results.Ok(await new KnA.Core.Ai.LlmClient(queue.Settings.Ai).ListModelsAsync(ct)); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
    {
        return Results.Problem(L.T("Cannot reach the LLM server at {0}: {1}", queue.Settings.Ai.EffectiveUrl, ex.Message), statusCode: 502);
    }
});
app.MapPost("/api/ai/suggest", async (AiSuggestRequest req, CancellationToken ct) =>
{
    var media = req.JobId is { } jid ? queue.Find(jid)?.Media : null;
    try { return Results.Ok(await new KnA.Core.Ai.Assistant(queue.Settings.Ai, queue.Settings.Capabilities).SuggestAsync(req.Prompt, req.Settings, media, ct)); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});
app.MapPost("/api/jobs/{id:guid}/ai/analyze", async (Guid id, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    var a = await queue.AnalyzeAsync(j, ct);
    var frames = new List<byte[]>();
    foreach (var f in new[] { 0.12, 0.37, 0.62, 0.87 })
        frames.Add(await File.ReadAllBytesAsync(await queue.Preview.ThumbnailAsync(id, a.Source.Duration.TotalSeconds * f, 768, ct), ct));
    try { return Results.Ok(await new KnA.Core.Ai.Assistant(queue.Settings.Ai, queue.Settings.Capabilities).AnalyzeAsync(a.Source, j.Settings, frames, ct)); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});
app.MapGet("/api/ai/runtime", async (CancellationToken ct) =>
{
    var py = KnA.Core.Ai.UpscaleSidecar.Python(queue.Settings.Ai, queue.Store.DataDirectory);
    var script = KnA.Core.Ai.UpscaleSidecar.Script();
    if (py is null || script is null) return Results.Ok(new { python = py, script, info = (object?)null });
    var r = await KnA.Core.Processing.ProcessRunner.RunAsync(py, [script, "--check"], timeout: TimeSpan.FromMinutes(1), ct: ct);
    object? info = KnA.Core.Ai.LlmClient.ExtractJson(r.StdOut);
    return Results.Ok(new { python = py, script, info, error = r.ExitCode == 0 ? null : r.StdErr.Trim() });
});

// ---------------------------------------------------------------- watch folders
app.MapGet("/api/watch", () => new { folders = watch.Folders, status = watch.Status, doneFolder = WatchFolderService.DoneFolderName });
app.MapPut("/api/watch", (List<WatchFolder> folders) =>
{
    foreach (var f in folders)
    {
        if (!browser.IsAllowed(f.Path)) return Results.Problem(L.T("Outside the allowed media roots: {0}", f.Path), statusCode: 403);
        if (f.OutputFolder is { Length: > 0 } o && !browser.IsWritable(o)) return Results.Problem(L.T("This folder is read-only for KnA."), statusCode: 403);
        if (f.AfterSuccess != WatchAfterSuccess.Keep && !browser.IsWritable(f.Path))
            return Results.Problem(L.T("Moving or deleting sources needs a writable folder: {0}", f.Path), statusCode: 403);
    }
    watch.Save(folders);
    return Results.Ok(new { folders = watch.Folders, status = watch.Status });
});
app.MapPost("/api/watch/{id}/scan", (string id) => { watch.ScanNow(id); return Results.Ok(); });

// ---------------------------------------------------------------- jobs
app.MapPost("/api/jobs", (AddJobsRequest req) =>
{
    var denied = req.Paths.Where(p => !browser.IsAllowed(p)).ToList();
    if (denied.Count > 0) return Results.Problem(L.T("Outside the allowed media roots: {0}", denied[0]), statusCode: 403);
    var added = queue.AddPaths(req.Paths, req.Settings ?? new JobSettings(), req.Extensions);
    return Results.Ok(new { added = added.Count });
});
app.MapPost("/api/jobs/remove", (IdsRequest req) => { queue.Remove(req.Ids); return Results.Ok(); });
app.MapPost("/api/jobs/clear", (ClearRequest req) => { queue.RemoveByStatus([.. req.Statuses]); return Results.Ok(); });
app.MapPost("/api/jobs/reset", (IdsRequest req) => { queue.Reset(req.Ids); return Results.Ok(); });
app.MapPost("/api/jobs/settings", (UpdateJobsRequest req) => { queue.UpdateSettings(req.Ids, _ => req.Settings); return Results.Ok(); });
app.MapPost("/api/jobs/{id:guid}/move", (Guid id, MoveRequest req) => { queue.Move(id, req.Delta); return Results.Ok(); });
app.MapGet("/api/jobs/{id:guid}/log", (Guid id) => queue.Find(id) is { } j ? Results.Text(j.Log) : Results.NotFound());
app.MapGet("/api/jobs/{id:guid}/command", async (Guid id) =>
{
    if (queue.Find(id) is not { } j) return Results.NotFound();
    try { return Results.Text(j.CommandLine ?? await queue.PreviewCommandAsync(j)); }
    catch (Exception ex) { return Results.Text(L.T("Cannot build the command: {0}", ex.Message)); }
});

// Editor: analysis and edits (tracks, external files, trim, settings of this file).
app.MapGet("/api/jobs/{id:guid}/analysis", async (Guid id, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    return Results.Ok(AnalysisDto.From(await queue.AnalyzeAsync(j, ct), j.SourcePath));
});
// Every option of the program that runs for a stream (read from its own help), for the parameter editors.
app.MapPost("/api/tools/options", async (ParameterOptionsRequest req, CancellationToken ct) =>
{
    var (program, options) = await queue.ParameterOptionsAsync(req.Tool, req.Settings, ct);
    return Results.Ok(new { program, options = options.Select(o => new { o.Name, o.Argument, o.Description, o.Values, o.Default, o.Category }),
        categories = KnA.Core.Tools.ToolHelp.Categories.Select(c => new { value = c, label = L.T(c) }) });
});
// Editor: the profile's automatic choices written into the fields (on open, after a profile or preset import).
app.MapPost("/api/jobs/{id:guid}/materialize", async (Guid id, MaterializeRequest req, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    var settings = req.Settings ?? j.Settings;
    var edit = req.Edit ?? j.Edit;
    var files = edit.ExternalFiles.Concat(edit.Tracks?.Select(t => t.File) ?? []).Distinct().Where(f => f != j.SourcePath && KnA.Core.Media.Frameserver.KindOf(f) == KnA.Core.Media.FrameserverKind.None);
    if (files.FirstOrDefault(f => !browser.IsAllowed(f)) is { } bad)
        return Results.Problem(L.T("Outside the allowed media roots: {0}", bad), statusCode: 403);
    if (req.Profile is { } kind) (settings, edit) = EditorModel.ApplyProfile(settings, edit, kind);
    if (req.Preset is { Length: > 0 } name)
    {
        var preset = queue.Presets.Get(name) ?? throw new KeyNotFoundException(name);
        var parts = (req.Parts ?? []).Aggregate(PresetParts.None, (acc, x) => acc | x);
        (settings, edit) = EditorModel.ApplyPreset(settings, edit, preset, parts == PresetParts.None ? PresetParts.All : parts);
    }
    var (s2, e2, a) = await EditorModel.MaterializeAsync(queue, id, settings, edit, ct);
    return Results.Ok(new { settings = s2, edit = e2, analysis = AnalysisDto.From(a, j.SourcePath) });
});
// Editor: the constant-quality value and speed KnA uses for the settings' quality level (rate control fields).
app.MapPost("/api/jobs/{id:guid}/rate", (Guid id, SizeRequest req) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    var s = req.Settings;
    return Results.Ok(EditorModel.ExplicitRate(queue, s, j.Media?.PrimaryVideo, s.Container ?? s.EffectiveContainer));
});
// Editor: a preset from this file (per-track choices become the job-wide ones for other files).
app.MapPost("/api/jobs/{id:guid}/preset", (Guid id, PresetFromJobRequest req) =>
{
    var saved = queue.Presets.Save(new Preset(req.Name, null, EditorModel.ForPreset(req.Settings, req.Edit) with { PresetName = req.Name }));
    hub.Broadcast("presets", queue.Presets.All);
    return Results.Ok(saved);
});
// The audio encoders available on this server for each codec (the editor's per-track encoder list).
app.MapGet("/api/audio/encoders", () =>
{
    var set = JobRunner.AudioEncodersFor(queue.Settings.Capabilities, queue.Tools);
    return Results.Ok(Enum.GetValues<AudioCodec>().Where(c => c is not (AudioCodec.Auto or AudioCodec.Copy))
        .ToDictionary(c => c.ToString(), c => set.Available(c).Select(e => new { value = e, label = AudioEncoderSet.Describe(e) })));
});
// VapourSynth / AviSynth filter catalogue and the script written from a chain.
app.MapGet("/api/frameserver/catalog", () => Results.Ok(new
{
    sources = KnA.Core.Media.FilterCatalog.Sources.Select(x => new { value = x.Id, label = x.Name }),
    filters = KnA.Core.Media.FilterCatalog.All.Select(f => new
    {
        f.Id, Kind = f.Kind.ToString(), Category = f.EnglishCategory, CategoryLabel = f.Category, f.Name, f.Description, f.Requires,
        Params = f.Params.Select(x => new { x.Name, x.Label, Type = x.Type.ToString(), x.Default, x.Min, x.Max, x.Choices, x.Help }),
    }),
}));
app.MapPost("/api/frameserver/script", (KnA.Core.Media.FrameserverSettings fs) => Results.Ok(new { script = fs.EffectiveScript ?? "" }));
// What a maximum file size leaves for each stream of a job.
app.MapPost("/api/jobs/{id:guid}/size", (Guid id, SizeRequest req) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    return queue.SizeBudget(j, req.Settings, req.Edit) is { } b
        ? Results.Ok(new { text = SizePlanner.Describe(b), b.VideoKbps, b.TooSmall })
        : Results.Ok(new { text = (string?)null, VideoKbps = 0, TooSmall = false });
});
// Subtitle OCR of one picture track (PGS) into an SRT next to the source, outside the queue.
app.MapPost("/api/jobs/{id:guid}/ocr", async (Guid id, OcrRequest req, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    if (!browser.IsAllowed(req.File)) return Results.Problem(L.T("Outside the allowed media roots: {0}", req.File), statusCode: 403);
    var dir = Path.GetDirectoryName(j.SourcePath) ?? "";
    if (!browser.IsWritable(dir)) return Results.Problem(L.T("Outside the allowed media roots: {0}", dir), statusCode: 403);
    try
    {
        var r = await queue.OcrTrackAsync(j, req.File, req.Stream, req.Language, null, null, ct);
        return Results.Ok(new { path = r.SrtPath, r.Subtitles, r.Pictures, r.Language });
    }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 400); }
});
// ---------------------------------------------------------------- OCR editor (Subtitle Edit's OCR window, in the browser)
object OcrLineDto(KnA.Core.Ocr.OcrLine l) => new { l.Index, start = l.Start.TotalSeconds, end = l.End.TotalSeconds, l.Text, l.Confidence, l.Italic, l.Forced, l.Edited };
app.MapPost("/api/ocr/sessions", async (OcrOpenRequest req, CancellationToken ct) =>
{
    var j = queue.Find(req.JobId) ?? throw new KeyNotFoundException();
    if (!browser.IsAllowed(req.File)) return Results.Problem(L.T("Outside the allowed media roots: {0}", req.File), statusCode: 403);
    var s = await KnA.Core.Ocr.OcrSession.OpenAsync(queue, j, req.File, req.Stream, ct);
    queue.OcrSessions[s.Id] = s;
    return Results.Ok(new { s.Id, count = s.Lines.Count, s.Language, s.Codec, options = queue.Settings.Ocr });
});
app.MapGet("/api/ocr/{sid:guid}", (Guid sid) => queue.OcrSessions.TryGetValue(sid, out var s)
    ? Results.Ok(new { s.Busy, s.Done, lines = s.Lines.Select(OcrLineDto), toCheck = s.Lines.Count(s.ToCheck) }) : Results.NotFound());
app.MapGet("/api/ocr/{sid:guid}/image/{i:int}", (Guid sid, int i) => queue.OcrSessions.TryGetValue(sid, out var s)
    ? Results.File(s.Png(i), "image/png") : Results.NotFound());
app.MapPost("/api/ocr/{sid:guid}/run", (Guid sid, OcrRunRequest req) =>
{
    if (!queue.OcrSessions.TryGetValue(sid, out var s)) return Results.NotFound();
    if (s.Busy) return Results.Ok(new { started = false });
    var options = req.Options ?? queue.Settings.Ocr;
    queue.Settings.Ocr = options with { Language = null };   // remembered as the default (jobs use it too)
    queue.SaveSettings();
    _ = Task.Run(async () => { try { await s.RecognizeAsync(queue, options, req.Only, null, CancellationToken.None); } catch (Exception ex) { Console.Error.WriteLine("OCR: " + ex.Message); } });
    return Results.Ok(new { started = true });
});
app.MapPut("/api/ocr/{sid:guid}/lines/{i:int}", (Guid sid, int i, OcrEditRequest req) =>
{
    if (!queue.OcrSessions.TryGetValue(sid, out var s)) return Results.NotFound();
    s.Edit(i, req.Text, req.Italic, req.Start is { } a ? TimeSpan.FromSeconds(a) : null, req.End is { } b ? TimeSpan.FromSeconds(b) : null);
    return s.Line(i) is { } l ? Results.Ok(OcrLineDto(l)) : Results.NotFound();
});
app.MapPost("/api/ocr/{sid:guid}/replace", (Guid sid, OcrReplaceRequest req) =>
{
    if (!queue.OcrSessions.TryGetValue(sid, out var s)) return Results.NotFound();
    try { return Results.Ok(new { changed = s.Replace(req.Find, req.Replace ?? "", req.Regex, false) }); }
    catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
});
app.MapPost("/api/ocr/{sid:guid}/save", async (Guid sid, bool? use, CancellationToken ct) =>
{
    if (!queue.OcrSessions.TryGetValue(sid, out var s)) return Results.NotFound();
    var path = s.DefaultSrtPath();
    if (!browser.IsWritable(Path.GetDirectoryName(path) ?? "")) return Results.Problem(L.T("Outside the allowed media roots: {0}", path), statusCode: 403);
    if (use == true) await s.UseInJobAsync(queue, path, ct); else await s.SaveAsync(path, ct);
    return Results.Ok(new { path });
});
app.MapDelete("/api/ocr/{sid:guid}", (Guid sid) => { queue.OcrSessions.TryRemove(sid, out _); return Results.Ok(); });

// Compressibility check: short samples encoded with these settings predict the final size.
app.MapPost("/api/jobs/{id:guid}/estimate", async (Guid id, SizeRequest req, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    try
    {
        var e = await SizeEstimator.EstimateAsync(queue, j, req.Settings, req.Edit ?? j.Edit, ct: ct);
        return Results.Ok(new { text = e.Describe(), e.Bytes });
    }
    catch (InvalidOperationException ex) { return Results.Ok(new { text = ex.Message, Bytes = 0L }); }
});
app.MapPut("/api/jobs/{id:guid}/edit", async (Guid id, EditRequest req, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    var files = req.Edit.ExternalFiles.Concat(req.Edit.Tracks?.Select(t => t.File) ?? []).Distinct().Where(f => f != j.SourcePath);
    if (files.FirstOrDefault(f => !browser.IsAllowed(f)) is { } bad)
        return Results.Problem(L.T("Outside the allowed media roots: {0}", bad), statusCode: 403);
    if (req.Edit.OutputFolder is { Length: > 0 } of && !browser.IsWritable(of))
        return Results.Problem(L.T("Outside the allowed media roots: {0}", of), statusCode: 403);
    var a = await queue.SetEditAsync(id, req.Edit, ct, req.Settings);
    return Results.Ok(AnalysisDto.From(a, j.SourcePath));
});

app.MapPost("/api/jobs/{id:guid}/split-segments", (Guid id) =>
    Results.Ok(queue.SplitSegments(id).Select(j => j.Id)));

// Previews (cached PNG/JPEG/MP4 files).
app.MapGet("/api/jobs/{id:guid}/preview/frame", async (Guid id, double t, bool? final, int? w, CancellationToken ct) =>
    Results.File(await queue.Preview.FrameAsync(id, t, final ?? true, Math.Clamp(w ?? 1280, 160, 3840), ct), "image/png"));
app.MapGet("/api/jobs/{id:guid}/preview/thumb", async (Guid id, double t, int? w, CancellationToken ct) =>
    Results.File(await queue.Preview.ThumbnailAsync(id, t, Math.Clamp(w ?? 192, 64, 640), ct), "image/jpeg"));
app.MapGet("/api/jobs/{id:guid}/preview/waveform", async (Guid id, string file, int stream, int? w, int? h, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    if (file != j.SourcePath && !j.Edit.ExternalFiles.Contains(file)) return Results.Problem("Unknown file", statusCode: 403);
    return Results.File(await queue.Preview.WaveformAsync(id, file, stream, Math.Clamp(w ?? 1600, 200, 4000), Math.Clamp(h ?? 100, 40, 400), ct), "image/png");
});
app.MapGet("/api/jobs/{id:guid}/preview/clip", async (Guid id, double t, double? len, CancellationToken ct) =>
    Results.File(await queue.Preview.ClipAsync(id, t, len ?? 6, ct), "video/mp4", enableRangeProcessing: true));
app.MapGet("/api/jobs/{id:guid}/cropdetect", async (Guid id, CancellationToken ct) =>
    Results.Ok(await queue.Preview.DetectCropAsync(id, ct)));

// ---------------------------------------------------------------- queue control
app.MapPost("/api/queue/start", () =>
{
    try { queue.Start(); return Results.Ok(); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
});
app.MapPost("/api/queue/stop", async () => { await queue.StopAsync(); return Results.Ok(); });
app.MapPost("/api/queue/pause", () => { queue.Pause(); return Results.Ok(); });
app.MapPost("/api/queue/resume", () => { queue.Resume(); return Results.Ok(); });

// ---------------------------------------------------------------- presets & wizard
app.MapGet("/api/presets", () => queue.Presets.All);
app.MapPost("/api/presets", (Preset p) =>
{
    var saved = queue.Presets.Save(p);
    hub.Broadcast("presets", queue.Presets.All);
    return Results.Ok(saved);
});
app.MapDelete("/api/presets/{name}", (string name) =>
{
    var ok = queue.Presets.Delete(name);
    hub.Broadcast("presets", queue.Presets.All);
    return ok ? Results.Ok() : Results.NotFound();
});
// Preset wizard: the questions that apply to the answers so far (with their defaults), and the resulting settings.
app.MapPost("/api/presets/wizard/steps", (Dictionary<string, string> answers) =>
{
    var caps = queue.Settings.Capabilities;
    var audio = JobRunner.AudioEncodersFor(caps, queue.Tools);
    var a = new Dictionary<string, string>(answers);
    IReadOnlyList<WizardStep> steps = [];
    for (int round = 0; round < 4; round++)
    {
        steps = PresetWizard.Steps(a, caps, audio);
        bool added = false;
        foreach (var st in steps)
            if (st.Kind is WizardStepKind.Choice or WizardStepKind.Number && !a.ContainsKey(st.Key)) { a[st.Key] = st.Default; added = true; }
        if (!added) break;
    }
    foreach (var key in a.Keys.Except(answers.Keys).ToList()) if (steps.All(x => x.Key != key)) a.Remove(key);
    var settings = PresetWizard.Build(a, caps);
    return Results.Ok(new { steps, answers = a, summary = PresetWizard.Summary(settings), settings });
});
app.MapPost("/api/presets/wizard", (Dictionary<string, string> answers) => PresetWizard.Build(answers, queue.Settings.Capabilities));

// ---------------------------------------------------------------- settings
app.MapPut("/api/settings", (SettingsUpdate u) =>
{
    var s = queue.Settings;
    if (u.OutputDirectory is not null)
    {
        if (u.OutputDirectory.Length > 0 && !browser.IsWritable(u.OutputDirectory))
            return Results.Problem(L.T("Output folder outside the allowed media roots."), statusCode: 403);
        s.OutputDirectory = u.OutputDirectory.Length == 0 ? null : u.OutputDirectory;
    }
    if (u.Defaults is not null) s.Defaults = u.Defaults;
    if (u.PostAction is { } pa) s.PostAction = powerActionsAllowed ? pa : PostQueueAction.Nothing;
    if (u.FolderExtensions is not null) s.FolderExtensions = u.FolderExtensions;
    if (u.Theme is not null) s.Theme = u.Theme;
    if (u.CheckUpdatesOnStart is { } cu) s.CheckUpdatesOnStart = cu;
    if (u.AutoInstallUpdates is { } ai2) s.AutoInstallUpdates = ai2;
    if (u.Language is { } lg) s.Language = lg is "it" or "en" ? lg : "Auto";
    if (u.RestoreQueue is { } rq) s.RestoreQueue = rq;
    if (u.OutputNameTemplate is { } nt) s.OutputNameTemplate = string.IsNullOrWhiteSpace(nt) ? OutputNaming.DefaultTemplate : nt;
    if (u.Priority is { } pr) s.Priority = pr;
    if (u.WorkLocation is { } wl) s.WorkLocation = wl;
    if (u.HardwareDecoding is { } hd) s.HardwareDecoding = hd;
    if (u.ResumableEncoding is { } re) s.ResumableEncoding = re;
    if (u.StallTimeoutMinutes is { } st) s.StallTimeoutMinutes = Math.Clamp(st, 1, 600);
    if (u.KeepLogs is { } kl) s.KeepLogs = Math.Clamp(kl, 10, 100000);
    if (u.TempDirectory is not null)
    {
        if (u.TempDirectory.Length > 0 && !advancedAllowed) return Results.Problem(L.T("Locked: set KNA_ALLOW_ADVANCED=true on the server to edit these from the browser."), statusCode: 403);
        s.TempDirectory = u.TempDirectory.Length == 0 ? null : u.TempDirectory;
    }
    if (u.VaapiDevice is not null) s.VaapiDevice = u.VaapiDevice.Length == 0 ? null : u.VaapiDevice;
    if (u.FfmpegChannel is { } ch) s.FfmpegChannel = ch == "master" ? "master" : "release";
    if (u.DiscAllTitles is { } dat) s.DiscAllTitles = dat;
    if (u.DiscMinMinutes is { } dmm) s.DiscMinMinutes = Math.Clamp(dmm, 0, 600);
    if (u.Ai is { } ai)
    {
        // The browser never receives the key: an empty value keeps the stored one, "-" clears it.
        var key = ai.ApiKey switch { null or "" => s.Ai.ApiKey, "-" => null, var k => k };
        // Python and model paths run programs on the server: advanced only.
        s.Ai = ai with { ApiKey = key, Python = advancedAllowed ? ai.Python : s.Ai.Python };
    }
    if (advancedAllowed)
    {
        if (u.FfmpegPath is not null) s.FfmpegPath = u.FfmpegPath.Length == 0 ? null : u.FfmpegPath;
        if (u.ExtraArguments is not null) s.ExtraArguments = u.ExtraArguments;
        // They run programs on the server: advanced only, like the paths above.
        if (u.AfterJobCommand is not null) s.AfterJobCommand = u.AfterJobCommand;
        if (u.AfterQueueCommand is not null) s.AfterQueueCommand = u.AfterQueueCommand;
        if (u.CustomFfmpegBuilds is not null) s.CustomFfmpegBuilds = u.CustomFfmpegBuilds.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToList();
        if (u.ToolPaths is not null)
            s.ToolPaths = u.ToolPaths.Where(kv => ToolIds.All.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
    }
    queue.SaveSettings();
    queue.RefreshTools();
    return Results.Ok(new { settings = Mapping.PublicSettings(s, SettingsStore.Json), tools = Mapping.Tools(queue), advancedAllowed });
});

// ---------------------------------------------------------------- hardware, updates, components
app.MapPost("/api/hardware/detect", async (CancellationToken ct) =>
{
    var progress = new Progress<string>(line => hub.Broadcast("detect", new { line }));
    var caps = await queue.DetectHardwareAsync(progress, ct);
    return caps is null ? Results.Problem(L.T("FFmpeg was not found. Configure it in Settings."), statusCode: 409) : Results.Ok(caps);
});

app.MapGet("/api/updates", async (CancellationToken ct) =>
{
    try
    {
        var u = await AppUpdater.CheckAsync("server", ct);
        queue.Settings.LastUpdateCheck = DateTimeOffset.Now;
        queue.SaveSettings();
        var perm = AppUpdater.CheckPermission();
        return Results.Ok(new
        {
            current = AppInfo.Version, latest = u.Latest?.Tag, isNewer = u.IsNewer, message = u.Message,
            url = u.Latest?.HtmlUrl, notes = u.Latest?.Body,
            canInstall = perm.CanInstall && u.Asset is not null, reason = perm.Reason, isAdministrator = perm.IsAdministrator,
            autoInstall = queue.Settings.AutoInstallUpdates, lastAuto = autoUpdate.Message, installed = autoUpdate.State == AutoUpdateState.Installed,
            howTo = IsContainer() ? L.T("Update the container: git pull && docker compose up -d --build")
                                  : L.T("Update the service: sudo kna-update"),
        });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
    {
        return Results.Problem(L.T("Cannot reach GitHub: {0}", ex.Message), statusCode: 502);
    }
});

app.MapGet("/api/components", async (CancellationToken ct) =>
{
    try { return Results.Ok(await queue.Components.CheckAsync(queue.Tools, online: true, ct)); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Results.Ok(await queue.Components.CheckAsync(queue.Tools, online: false, ct));
    }
});

app.MapPost("/api/components/{id}/install", async (string id, CancellationToken ct) =>
{
    if (componentsLocked) return Results.Problem(L.T("Component installation is disabled on this server (KNA_LOCK_COMPONENTS)."), statusCode: 403);
    bool isModel = id.StartsWith("model:", StringComparison.Ordinal) || id.StartsWith("llm:", StringComparison.Ordinal);
    if (queue.IsRunning && !isModel) return Results.Problem(L.T("Stop the queue before updating components."), statusCode: 409);
    var progress = new Progress<double>(p => hub.Broadcast("component", new { id, percent = Math.Round(p, 1) }));
    var logger = new Progress<string>(l => hub.Broadcast("component", new { id, line = l }));
    try
    {
        await queue.Components.InstallAsync(id, progress, logger, ct);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or PlatformNotSupportedException)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
    queue.RefreshTools();
    if (id == "ffmpeg") await queue.DetectHardwareAsync(new Progress<string>(l => hub.Broadcast("detect", new { line = l })), ct);
    return Results.Ok(await queue.Components.CheckAsync(queue.Tools, online: false, ct));
});

// ---------------------------------------------------------------- lifecycle
queue.QueueCompleted += action =>
{
    if (!powerActionsAllowed || action == PostQueueAction.Nothing) return;
    log.LogInformation("Queue finished, post action: {Action}", action);
    PowerActions.Run(action, () => app.Lifetime.StopApplication());
};

app.Lifetime.ApplicationStarted.Register(async () =>
{
    var urls = string.Join(", ", app.Urls);
    log.LogInformation("KnA {Version} web UI listening on {Urls}", AppInfo.Version, urls);
    log.LogInformation("Data directory: {Dir}", queue.Store.DataDirectory);
    log.LogInformation("Media roots: {Roots}", string.Join(", ", browser.Roots));
    log.LogInformation("FFmpeg: {Ffmpeg}", queue.Tools?.Ffmpeg ?? "NOT FOUND");
    if (string.IsNullOrEmpty(password) && browser.Roots.Any(r => r == Path.GetPathRoot(r)))
        log.LogWarning("The whole file system is reachable from the browser and no password is set: set KNA_PASSWORD or KNA_MEDIA_ROOTS.");
    if (queue.Tools is not null && queue.Settings.Capabilities is not { IsCurrent: true })
    {
        log.LogInformation("First start: detecting hardware encoders…");
        try { await queue.DetectHardwareAsync(new Progress<string>(l => log.LogInformation("{Line}", l))); }
        catch (Exception ex) { log.LogWarning(ex, "Hardware detection failed"); }
    }
    if (args.Contains("--open"))
    {
        var url = app.Urls.FirstOrDefault()?.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost") ?? "http://localhost:8585";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
    }
});
app.Lifetime.ApplicationStopping.Register(() =>
{
    queue.StopAsync().Wait(TimeSpan.FromSeconds(10));
    queue.SaveQueue();
});

// ---------------------------------------------------------------- automatic updates
// Every 6 hours (at most once a day against GitHub): a newer server package is installed in place when the program
// folder may be written (portable copy, Windows service in ProgramData); /opt/kna, Docker and read-only folders only
// get the message. Once installed, a service restarts itself as soon as the queue is idle (systemd Restart=on-failure,
// Windows service recovery: both restart a process that exits with an error code).
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromMinutes(2));
    while (true)
    {
        try
        {
            var st = queue.Settings;
            if (st.CheckUpdatesOnStart && !(st.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(20)) && autoUpdate.State != AutoUpdateState.Installed)
            {
                var r = await AppUpdater.AutoUpdateAsync("server", store.DataDirectory, st.AutoInstallUpdates);
                st.LastUpdateCheck = DateTimeOffset.Now;
                queue.SaveSettings();
                autoUpdate = r;
                if (r.State != AutoUpdateState.UpToDate) Console.WriteLine("[KnA] " + r.Message);
            }
            bool service = Environment.GetEnvironmentVariable("INVOCATION_ID") is not null
                           || (OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());
            if (autoUpdate.State == AutoUpdateState.Installed && service && !queue.IsRunning && !queue.OcrSessions.Values.Any(o => o.Busy))
            {
                Console.WriteLine("[KnA] Restarting into the new version.");
                Environment.Exit(75);   // EX_TEMPFAIL: the service manager starts it again, now the new files
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine("[KnA] update check: " + ex.Message); }
        await Task.Delay(autoUpdate.State == AutoUpdateState.Installed ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(6));
    }
});

app.Run();
return 0;

static bool Env(string name) => AppEnv.Flag(name.StartsWith("KNA_") ? name[4..] : name);

async Task<IResult> Download(string path, HttpContext ctx, CancellationToken ct)
{
    if (!browser.IsAllowed(path)) return Results.Problem(L.T("Outside the allowed media roots: {0}", path), statusCode: 403);
    if (File.Exists(path))
        return Results.File(path, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true);
    if (!Directory.Exists(path)) return Results.NotFound();
    var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) + ".zip";
    ctx.Response.ContentType = "application/zip";
    ctx.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
    // Videos don't compress: store them, deflate the small text files.
    using (var zip = new System.IO.Compression.ZipArchive(ctx.Response.BodyWriter.AsStream(), System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            ct.ThrowIfCancellationRequested();
            if (!browser.IsAllowed(f)) continue;
            var kind = FileBrowser.KindOf(f, false);
            var entry = zip.CreateEntry(Path.GetRelativePath(path, f).Replace('\\', '/'),
                kind is "text" or "subtitle" or "other" ? System.IO.Compression.CompressionLevel.Fastest : System.IO.Compression.CompressionLevel.NoCompression);
            await using var es = entry.Open();
            await using var fs = File.OpenRead(f);
            await fs.CopyToAsync(es, ct);
        }
    }
    return Results.Empty;
}

static bool IsContainer() => File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

static string Platform() =>
    (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux") + (IsContainer() ? " (container)" : "")
    + " · " + KnA.Core.Tools.CpuFeatures.Description;

public partial class Program;
