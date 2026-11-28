using System.Diagnostics;
using System.Globalization;
using System.Text;
using KnA.Core.Accounts;
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

// Lost the administrator's password: kna-server --reset-password NAME (asks for the new one; an unknown NAME becomes a
// new administrator). Run it as the service user, with the same KNA_DATA_DIR.
if (args is ["--reset-password", var resetName, ..])
    return ResetPassword(resetName);

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
var accounts = new AccountStore(store.DataDirectory);
var throttle = new LoginThrottle();
builder.Services.AddSingleton<WatchFolderService>();

var app = builder.Build();
var queue = app.Services.GetRequiredService<QueueService>();
AutoUpdateResult autoUpdate = new(AutoUpdateState.UpToDate, "", new UpdateCheck(AppUpdater.CurrentVersion, null, false, null, ""));
var hub = app.Services.GetRequiredService<EventHub>();
// Each account's own presets and settings (users); administrators use the shared ones.
var userData = new UserDataStore(store.DataDirectory, queue.Presets);
JobDto.OwnerNames = id => accounts.Find(id)?.Name;
IReadOnlyList<Preset> PresetsFor(AppUser u) => userData.Visible(u.Id, u.HasOwnData);
Preset? PresetOf(AppUser u, string name) => userData.Get(u.Id, u.HasOwnData, name);
PresetStore PresetStoreOf(AppUser u) => u.HasOwnData ? userData.PresetsOf(u.Id) : queue.Presets;
JobSettings DefaultsFor(AppUser u) => u.HasOwnData && userData.SettingsOf(u.Id).Defaults is { } d ? d : queue.Settings.Defaults;
void PresetsChanged() => hub.BroadcastEach("presets", PresetsFor);
// The server's settings as an account sees them: a user's own quick-form defaults and theme in place of the shared ones.
System.Text.Json.Nodes.JsonNode? SettingsFor(AppUser u)
{
    var node = Mapping.PublicSettings(queue.Settings, SettingsStore.Json);
    if (u.HasOwnData && node is System.Text.Json.Nodes.JsonObject o)
    {
        var mine = userData.SettingsOf(u.Id);
        if (mine.Defaults is { } d) o["defaults"] = System.Text.Json.JsonSerializer.SerializeToNode(d, SettingsStore.Json);
        if (mine.Theme is { } th) o["theme"] = th;
    }
    return node;
}
// A job the signed-in account may see and manage (users: their own; administrators: everybody's).
QueueJob? JobOf(HttpContext ctx, Guid id) => queue.Find(id) is { } j && ctx.CurrentUser().Sees(j) ? j : null;
List<Guid> OwnIds(HttpContext ctx, IEnumerable<Guid> ids) => ids.Where(id => JobOf(ctx, id) is not null).ToList();
var browser = app.Services.GetRequiredService<FileBrowser>();
var userBrowsers = new UserBrowsers(browser);
// The folders the signed-in account may use (administrators: all of the server's).
FileBrowser B(HttpContext ctx) => userBrowsers.For(ctx.CurrentUser());
var uploads = app.Services.GetRequiredService<UploadService>();
var watch = app.Services.GetRequiredService<WatchFolderService>();
watch.StatusChanged += st => hub.ToAdmins("watch", st);
watch.JobsQueued += jobs => hub.ToAdmins("toast", new { text = L.T("Watch folder: {0} file(s) queued", jobs.Count) });
var log = app.Logger;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    log.LogCritical("Crash log: {Path}", queue.Store.WriteCrashLog((Exception)e.ExceptionObject, "server"));

bool advancedAllowed = Env("KNA_ALLOW_ADVANCED");
bool powerActionsAllowed = Env("KNA_ALLOW_POWER_ACTIONS");
bool componentsLocked = Env("KNA_LOCK_COMPONENTS");

// ---------------------------------------------------------------- accounts: migration of the old shared password
// KNA_PASSWORD (HTTP Basic, any user name) becomes the first administrator; after that the variable is not used.
if (Auth.Disabled)
    log.LogWarning("KNA_AUTH=off: no sign-in, everybody who reaches KnA is an administrator.");
else if (accounts.EnsureFromEnvironment(AppEnv.Get("PASSWORD"), AppEnv.Get("ADMIN_USER")) is { } migrated)
    log.LogInformation("KNA_PASSWORD: administrator account \"{Name}\" created with that password. Accounts and passwords are now managed in Settings → Accounts.", migrated.Name);
else if (AppEnv.Get("PASSWORD") is not null && !accounts.SetupRequired)
    log.LogInformation("KNA_PASSWORD is set but accounts already exist: it is not used any more (Settings → Accounts).");

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

app.UseAccounts(accounts, throttle);
// Every /api/jobs/{id}/… and /api/ocr/{session}/… request: a user reaches only their own jobs (another account's
// job answers as if it did not exist).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    QueueJob? job = null;
    if (Segment(path, "/api/jobs/") is { } jid) job = queue.Find(jid);
    else if (Segment(path, "/api/ocr/") is { } sid && queue.OcrSessions.TryGetValue(sid, out var session)) job = session.Job;
    if (job is not null && !ctx.CurrentUser().Sees(job))
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsJsonAsync(new { title = L.T("Not found."), detail = L.T("Not found.") });
        return;
    }
    await next();

    static Guid? Segment(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(path[prefix.Length..].Split('/')[0], out var g) ? g : null;
});
app.UseDefaultFiles();
app.UseStaticFiles();
// A deleted account's presets, settings and waiting jobs go with it (a file being encoded finishes).
app.MapAuth(accounts, throttle, userBrowsers.For, id =>
{
    userData.Delete(id);
    queue.Remove(queue.Jobs.Where(j => j.Owner == id).Select(j => j.Id));
    PresetsChanged();
});

// Hand-written scripts and arguments run on the server: an account without advanced mode may keep those already in
// the presets, the defaults or the job, never bring in new ones.
IResult? DenyAdvanced(HttpContext ctx, JobSettings? settings, JobEdit? edit = null, IEnumerable<QueueJob>? jobs = null, IEnumerable<string>? paths = null)
{
    var user = ctx.CurrentUser();
    if (user.MayUseAdvanced) return null;
    if (paths?.FirstOrDefault(AdvancedContent.IsScriptSource) is { } script)
        return Results.Problem(AdvancedContent.Denied(new(L.T("a script as the source"), script)), statusCode: 403);
    var allowed = PresetsFor(user).SelectMany(p => AdvancedContent.Of(p.Settings))
        .Concat(AdvancedContent.Of(queue.Settings.Defaults)).Concat(AdvancedContent.Of(DefaultsFor(user)))
        .Concat((jobs ?? []).SelectMany(j => AdvancedContent.Of(j.Settings, j.Edit)));
    return AdvancedContent.FirstNew(settings, edit, allowed) is { } item ? Results.Problem(AdvancedContent.Denied(item), statusCode: 403) : null;
}

// ---------------------------------------------------------------- state, i18n & events
app.MapGet("/api/state", (HttpContext ctx) =>
{
    var u = ctx.CurrentUser();
    return new StateDto(AppInfo.Version, Platform(), L.Language, SettingsFor(u), OptionsDto.Create(),
        Mapping.Tools(queue), Mapping.QueueState(queue, u.Sees), queue.Jobs.Where(u.Sees).Select(JobDto.From).ToList(), powerActionsAllowed, PresetsFor(u));
});

app.MapGet("/api/i18n", () => new { lang = L.Language, table = L.IsItalian() ? L.ItalianTable : new Dictionary<string, string>() });

app.MapGet("/api/events", (HttpContext ctx, CancellationToken ct) => hub.StreamAsync(ctx, ct));

app.MapGet("/api/frameserver/templates", () => KnA.Core.Media.Frameserver.Templates.Select(t => new { t.Name, Kind = t.Kind.ToString(), t.Body }));
app.MapGet("/api/info", (HttpContext ctx) =>
{
    var user = ctx.CurrentUser();
    var b = B(ctx);
    return new
    {
        // advancedAllowed: the server's advanced settings (paths, commands), administrators only.
        advancedAllowed = advancedAllowed && user.IsAdmin, powerActionsAllowed, componentsLocked, roots = b.Roots, writableRoots = b.WritableRoots,
        uploadDir = uploads.DefaultFolder, maxUploadBytes = uploads.MaxFileBytes == long.MaxValue ? (long?)null : uploads.MaxFileBytes,
        vspipe = queue.Tools?.VsPipe, fdkaac = queue.Tools?.Fdkaac,
        dataDirectory = user.IsAdmin ? queue.Store.DataDirectory : null, version = AppInfo.Version, platform = Platform(),
        user = new { user.Name, user.IsAdmin, advanced = user.MayUseAdvanced, user.AuthDisabled },
    };
});

// ---------------------------------------------------------------- files
app.MapGet("/api/browse", (string? path, bool? external, string? mode, bool? hidden, HttpContext ctx) =>
{
    try { return Results.Ok(B(ctx).Browse(path, mode ?? (external == true ? "external" : "video"), hidden ?? false)); }
    catch (UnauthorizedAccessException ex) { return Results.Problem(ex.Message, statusCode: 403); }
    catch (IOException ex) { return Results.Problem(ex.Message, statusCode: 400); }
});

// Downloads: a file (resumable, range requests) or a whole folder as a ZIP streamed on the fly.
app.MapGet("/api/files/download", (string path, HttpContext ctx, CancellationToken ct) => Download(path, ctx, ct));
app.MapGet("/api/jobs/{id:guid}/download", (Guid id, HttpContext ctx, CancellationToken ct) =>
    queue.Find(id)?.OutputPath is { } o ? Download(o, ctx, ct) : Task.FromResult(Results.NotFound()));

app.MapPost("/api/files/mkdir", (FolderRequest req, HttpContext ctx) =>
{
    if (!B(ctx).IsWritable(req.Path)) return Results.Problem(L.T("This folder is read-only for KnA."), statusCode: 403);
    Directory.CreateDirectory(req.Path);
    return Results.Ok(new { path = Path.GetFullPath(req.Path) });
});

// ---------------------------------------------------------------- uploads (chunked, resumable)
app.MapPost("/api/uploads", (UploadCreate req, HttpContext ctx) =>
{
    var folder = string.IsNullOrWhiteSpace(req.Folder) ? uploads.DefaultFolder : req.Folder;
    if (!B(ctx).IsWritable(folder)) return Results.Problem(L.T("This folder is read-only for KnA."), statusCode: 403);
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
app.MapPost("/api/uploads/{id}/complete", async (string id, UploadComplete req, HttpContext ctx, CancellationToken ct) =>
{
    if (req.Enqueue && DenyAdvanced(ctx, req.Settings, paths: [uploads.Get(id)?.Name ?? ""]) is { } denied) return denied;
    var user = ctx.CurrentUser();
    var path = uploads.Complete(id);
    int added = 0;
    var target = req.AttachTo is { } jid ? JobOf(ctx, jid)
        : req.AttachToSource is { Length: > 0 } src ? queue.Jobs.LastOrDefault(j => j.SourcePath == src && user.Sees(j)) : null;
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
        var settings = req.Preset is { Length: > 0 } pn && PresetOf(user, pn) is { } preset
            ? preset.Settings with { PresetName = preset.Name } : req.Settings ?? DefaultsFor(user);
        added = queue.AddPaths([path], settings, null, null, user.OwnerId).Count;
    }
    return Results.Ok(new { path, added });
});

// ---------------------------------------------------------------- smart drag & drop (server paths)
app.MapPost("/api/drop/classify", (DropRequest req, HttpContext ctx) =>
{
    if (req.Paths.FirstOrDefault(p => !B(ctx).IsAllowed(p)) is { } bad)
        return Results.Problem(L.T("Outside the allowed media roots: {0}", bad), statusCode: 403);
    return Results.Ok(DropClassifier.Classify(req.Paths, queue, req.TargetJob, ctx.CurrentUser().Sees));
});
app.MapPost("/api/drop/apply", async (DropRequest req, HttpContext ctx, CancellationToken ct) =>
{
    if (req.Paths.FirstOrDefault(p => !B(ctx).IsAllowed(p)) is { } bad)
        return Results.Problem(L.T("Outside the allowed media roots: {0}", bad), statusCode: 403);
    if (DenyAdvanced(ctx, req.Settings, paths: req.Paths) is { } denied) return denied;
    var user = ctx.CurrentUser();
    var plan = DropClassifier.Classify(req.Paths, queue, req.TargetJob, user.Sees);
    // Preset files bring their settings in: the same check as a preset saved from the editor.
    foreach (var item in plan.Items.Where(i => i.Kind == DropKind.Preset))
        if (DropClassifier.ReadPreset(item.Path) is { } dropped && DenyAdvanced(ctx, dropped.Settings) is { } deniedPreset) return deniedPreset;
    var r = await DropClassifier.ApplyAsync(plan, queue, req.Settings ?? DefaultsFor(user), ct, user.OwnerId, PresetStoreOf(user));
    if (r.Presets > 0) PresetsChanged();
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
// AI assistant guided set-up: install steps, connection check with a plain diagnosis, a test question to a model.
// The browser never receives the stored key: an empty key in the request means "the stored one".
KnA.Core.Ai.AiSettings WithStoredKey(KnA.Core.Ai.AiSettings ai) => ai with { ApiKey = string.IsNullOrEmpty(ai.ApiKey) ? queue.Settings.Ai.ApiKey : ai.ApiKey == "-" ? null : ai.ApiKey };
app.MapGet("/api/ai/setup/guide", (string provider) =>
{
    var (text, vision) = KnA.Core.Ai.AiSetup.Suggested(provider);
    return Results.Ok(new { steps = KnA.Core.Ai.AiSetup.Install(provider), needsKey = KnA.Core.Ai.AiSetup.NeedsKey(provider), suggestedText = text, suggestedVision = vision,
        url = (KnA.Core.Ai.AiSettings.Providers.FirstOrDefault(p => p.Id == provider)?.DefaultUrl) });
});
app.MapPost("/api/ai/setup/check", async (KnA.Core.Ai.AiSettings ai, CancellationToken ct) => Results.Ok(await KnA.Core.Ai.AiSetup.CheckAsync(WithStoredKey(ai), ct))).AdminOnly();
app.MapPost("/api/ai/setup/test", async (AiTestRequest req, CancellationToken ct) => Results.Ok(new { error = await KnA.Core.Ai.AiSetup.TestAsync(WithStoredKey(req.Ai), req.Model, ct) })).AdminOnly();
app.MapPost("/api/ai/suggest", async (AiSuggestRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var media = req.JobId is { } jid ? JobOf(ctx, jid)?.Media : null;
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
}).AdminOnly();

// ---------------------------------------------------------------- watch folders
app.MapGet("/api/watch", () => new { folders = watch.Folders, status = watch.Status, doneFolder = WatchFolderService.DoneFolderName }).AdminOnly();
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
}).AdminOnly();
app.MapPost("/api/watch/{id}/scan", (string id) => { watch.ScanNow(id); return Results.Ok(); }).AdminOnly();

// ---------------------------------------------------------------- jobs
app.MapPost("/api/jobs", (AddJobsRequest req, HttpContext ctx) =>
{
    var denied = req.Paths.Where(p => !B(ctx).IsAllowed(p)).ToList();
    if (denied.Count > 0) return Results.Problem(L.T("Outside the allowed media roots: {0}", denied[0]), statusCode: 403);
    if (DenyAdvanced(ctx, req.Settings, paths: req.Paths) is { } noAdvanced) return noAdvanced;
    var added = queue.AddPaths(req.Paths, req.Settings ?? new JobSettings(), req.Extensions, null, ctx.CurrentUser().OwnerId);
    return Results.Ok(new { added = added.Count });
});
app.MapPost("/api/jobs/remove", (IdsRequest req, HttpContext ctx) => { queue.Remove(OwnIds(ctx, req.Ids)); return Results.Ok(); });
app.MapPost("/api/jobs/clear", (ClearRequest req, HttpContext ctx) =>
{
    var user = ctx.CurrentUser();
    queue.Remove(queue.Jobs.Where(j => user.Sees(j) && req.Statuses.Contains(j.Status)).Select(j => j.Id));
    return Results.Ok();
});
app.MapPost("/api/jobs/reset", (IdsRequest req, HttpContext ctx) => { queue.Reset(OwnIds(ctx, req.Ids)); return Results.Ok(); });
app.MapPost("/api/jobs/settings", (UpdateJobsRequest req, HttpContext ctx) =>
{
    var ids = OwnIds(ctx, req.Ids);
    if (DenyAdvanced(ctx, req.Settings, jobs: ids.Select(queue.Find).OfType<QueueJob>()) is { } denied) return denied;
    queue.UpdateSettings(ids, _ => req.Settings);
    return Results.Ok();
});
app.MapPost("/api/jobs/{id:guid}/move", (Guid id, MoveRequest req, HttpContext ctx) =>
{
    var user = ctx.CurrentUser();
    queue.Move(id, req.Delta, user.IsAdmin ? null : user.Sees);   // a user moves past their own jobs
    return Results.Ok();
});
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
// "Use the GPU filter instead" (editor, Filters tab): a script filter replaced by the encoder's --vpp-* filter.
app.MapPost("/api/gpu-instead", (GpuInsteadRequest req) => Results.Ok(KnA.Core.Encoding.RigayaFilters.UseGpuInstead(req.Settings, req.Index)));
app.MapPost("/api/tools/options", async (ParameterOptionsRequest req, CancellationToken ct) =>
{
    var (program, options) = await queue.ParameterOptionsAsync(req.Tool, req.Settings, ct);
    return Results.Ok(new { program, options = options.Select(o => new { o.Name, o.Argument, o.Description, o.Values, o.Default, o.Category }),
        categories = KnA.Core.Tools.ToolHelp.Categories.Select(c => new { value = c, label = L.T(c) }) });
});
// Editor: the profile's automatic choices written into the fields (on open, after a profile or preset import).
app.MapPost("/api/jobs/{id:guid}/materialize", async (Guid id, MaterializeRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    var settings = req.Settings ?? j.Settings;
    var edit = req.Edit ?? j.Edit;
    var files = edit.ExternalFiles.Concat(edit.Tracks?.Select(t => t.File) ?? []).Distinct().Where(f => f != j.SourcePath && KnA.Core.Media.Frameserver.KindOf(f) == KnA.Core.Media.FrameserverKind.None);
    if (files.FirstOrDefault(f => !B(ctx).IsAllowed(f)) is { } bad)
        return Results.Problem(L.T("Outside the allowed media roots: {0}", bad), statusCode: 403);
    if (req.Profile is { } kind) (settings, edit) = EditorModel.ApplyProfile(settings, edit, kind);
    if (req.Preset is { Length: > 0 } name)
    {
        var preset = PresetOf(ctx.CurrentUser(), name) ?? throw new KeyNotFoundException(name);
        var parts = (req.Parts ?? []).Aggregate(PresetParts.None, (acc, x) => acc | x);
        (settings, edit) = EditorModel.ApplyPreset(settings, edit, preset, parts == PresetParts.None ? PresetParts.All : parts);
    }
    var (s2, e2, a) = await EditorModel.MaterializeAsync(queue, id, settings, edit, ct);
    return Results.Ok(new { settings = s2, edit = e2, analysis = AnalysisDto.From(a, j.SourcePath) });
});
// Editor: the constant-quality value and speed KnA uses for the settings' quality level (rate control fields).
// Editor (rigaya encoders): expected time of the job with each profile, from the optional GPU benchmark.
app.MapPost("/api/jobs/{id:guid}/gpu-estimate", (Guid id, SizeRequest req) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    return Results.Ok(new { text = j.Media is null ? null : KnA.Core.Encoding.GpuBenchmark.EstimateFor(req.Settings, j.Media, req.Edit ?? j.Edit, queue.Settings.GpuBenchmarks) });
});
app.MapPost("/api/gpu/benchmark", async (GpuBenchmarkRequest req, CancellationToken ct) =>
{
    try { return Results.Ok(await KnA.Core.Encoding.GpuBenchmark.RunAsync(queue, req.Backend, req.Codec, null, ct)); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 400); }
}).AdminOnly();
app.MapPost("/api/jobs/{id:guid}/rate", (Guid id, SizeRequest req) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    var s = req.Settings;
    return Results.Ok(EditorModel.ExplicitRate(queue, s, j.Media?.PrimaryVideo, s.Container ?? s.EffectiveContainer));
});
// Editor: a preset from this file (per-track choices become the job-wide ones for other files).
app.MapPost("/api/jobs/{id:guid}/preset", (Guid id, PresetFromJobRequest req, HttpContext ctx) =>
{
    if (DenyAdvanced(ctx, req.Settings, req.Edit, jobs: queue.Find(id) is { } pj ? [pj] : null) is { } denied) return denied;
    var saved = PresetStoreOf(ctx.CurrentUser()).Save(new Preset(req.Name, null, EditorModel.ForPreset(req.Settings, req.Edit) with { PresetName = req.Name }));
    PresetsChanged();
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
        f.Plugin, Available = KnA.Core.Tools.VapourSynthRuntime.Has(f.Plugin),
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
app.MapPost("/api/jobs/{id:guid}/ocr", async (Guid id, OcrRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    if (!B(ctx).IsAllowed(req.File)) return Results.Problem(L.T("Outside the allowed media roots: {0}", req.File), statusCode: 403);
    var dir = Path.GetDirectoryName(j.SourcePath) ?? "";
    if (!B(ctx).IsWritable(dir)) return Results.Problem(L.T("Outside the allowed media roots: {0}", dir), statusCode: 403);
    try
    {
        var r = await queue.OcrTrackAsync(j, req.File, req.Stream, req.Language, null, null, ct);
        return Results.Ok(new { path = r.SrtPath, r.Subtitles, r.Pictures, r.Language });
    }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 400); }
});
// ---------------------------------------------------------------- OCR editor (Subtitle Edit's OCR window, in the browser)
object OcrLineDto(KnA.Core.Ocr.OcrLine l) => new { l.Index, start = l.Start.TotalSeconds, end = l.End.TotalSeconds, l.Text, l.Confidence, l.Italic, l.Forced, l.Edited };
app.MapPost("/api/ocr/sessions", async (OcrOpenRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var j = JobOf(ctx, req.JobId) ?? throw new KeyNotFoundException();
    if (!B(ctx).IsAllowed(req.File)) return Results.Problem(L.T("Outside the allowed media roots: {0}", req.File), statusCode: 403);
    var s = await KnA.Core.Ocr.OcrSession.OpenAsync(queue, j, req.File, req.Stream, ct, req.Copy);
    queue.OcrSessions[s.Id] = s;
    return Results.Ok(new { s.Id, count = s.Lines.Count, s.Language, s.Codec, options = queue.Settings.Ocr });
});
app.MapGet("/api/ocr/{sid:guid}", (Guid sid) => queue.OcrSessions.TryGetValue(sid, out var s)
    ? Results.Ok(new { s.Busy, s.Done, lines = s.Lines.Select(OcrLineDto), toCheck = s.Lines.Count(s.ToCheck) }) : Results.NotFound());
app.MapGet("/api/ocr/{sid:guid}/image/{i:int}", (Guid sid, int i) => queue.OcrSessions.TryGetValue(sid, out var s)
    ? Results.File(s.Png(i), "image/png") : Results.NotFound());
app.MapPost("/api/ocr/{sid:guid}/run", (Guid sid, OcrRunRequest req, HttpContext ctx) =>
{
    if (!queue.OcrSessions.TryGetValue(sid, out var s)) return Results.NotFound();
    if (s.Busy) return Results.Ok(new { started = false });
    var options = req.Options ?? queue.Settings.Ocr;
    if (ctx.CurrentUser().IsAdmin)
    {
        queue.Settings.Ocr = options with { Language = null };   // remembered as the server's default (jobs use it too)
        queue.SaveSettings();
    }
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
app.MapPost("/api/ocr/{sid:guid}/save", async (Guid sid, bool? use, HttpContext ctx, CancellationToken ct) =>
{
    if (!queue.OcrSessions.TryGetValue(sid, out var s)) return Results.NotFound();
    var path = s.DefaultSrtPath();
    if (!B(ctx).IsWritable(Path.GetDirectoryName(path) ?? "")) return Results.Problem(L.T("Outside the allowed media roots: {0}", path), statusCode: 403);
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
app.MapPut("/api/jobs/{id:guid}/edit", async (Guid id, EditRequest req, HttpContext ctx, CancellationToken ct) =>
{
    var j = queue.Find(id) ?? throw new KeyNotFoundException();
    var files = req.Edit.ExternalFiles.Concat(req.Edit.Tracks?.Select(t => t.File) ?? []).Distinct().Where(f => f != j.SourcePath);
    if (files.FirstOrDefault(f => !B(ctx).IsAllowed(f)) is { } bad)
        return Results.Problem(L.T("Outside the allowed media roots: {0}", bad), statusCode: 403);
    if (req.Edit.OutputFolder is { Length: > 0 } of && !B(ctx).IsWritable(of))
        return Results.Problem(L.T("Outside the allowed media roots: {0}", of), statusCode: 403);
    if (DenyAdvanced(ctx, req.Settings, req.Edit, jobs: [j], paths: files) is { } denied) return denied;
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
// One engine encodes every account's jobs in queue order. A user starts it (their stopped jobs go on), stops only
// their own jobs (the others go on) and pauses only while their own file is being encoded; administrators control all.
app.MapPost("/api/queue/start", (HttpContext ctx) =>
{
    var user = ctx.CurrentUser();
    try { if (user.IsAdmin) queue.Start(); else queue.Start(user.Sees); return Results.Ok(); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
});
app.MapPost("/api/queue/stop", async (HttpContext ctx) =>
{
    var user = ctx.CurrentUser();
    if (user.IsAdmin) await queue.StopAsync(); else queue.StopJobs(user.Sees);
    return Results.Ok();
});
IResult? DenyOthersJob(HttpContext ctx) =>
    queue.CurrentJob is { } current && !ctx.CurrentUser().Sees(current)
        ? Results.Problem(L.T("The file being encoded belongs to another account: only an administrator can pause it."), statusCode: 403) : null;
app.MapPost("/api/queue/pause", (HttpContext ctx) => { if (DenyOthersJob(ctx) is { } d) return d; queue.Pause(); return Results.Ok(); });
app.MapPost("/api/queue/resume", (HttpContext ctx) => { if (DenyOthersJob(ctx) is { } d) return d; queue.Resume(); return Results.Ok(); });

// ---------------------------------------------------------------- presets & wizard
// Users save and delete their own presets (one with a shared preset's name takes its place for them); administrators
// manage the shared ones, which every account sees, and every account's own (Settings → Accounts).
app.MapGet("/api/presets", (HttpContext ctx) => PresetsFor(ctx.CurrentUser()));
app.MapPost("/api/presets", (Preset p, HttpContext ctx) =>
{
    if (DenyAdvanced(ctx, p.Settings) is { } denied) return denied;
    var saved = PresetStoreOf(ctx.CurrentUser()).Save(p);
    PresetsChanged();
    return Results.Ok(saved);
});
app.MapDelete("/api/presets/{name}", (string name, HttpContext ctx) =>
{
    var user = ctx.CurrentUser();
    if (user.HasOwnData && !userData.PresetsOf(user.Id).User.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        && queue.Presets.User.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        return Results.Problem(L.T("\"{0}\" is a shared preset: only an administrator can delete it.", name), statusCode: 403);
    var ok = PresetStoreOf(user).Delete(name);
    PresetsChanged();
    return ok ? Results.Ok() : Results.NotFound();
});
// Administrators: an account's own presets, queue and settings.
app.MapGet("/api/accounts/{id:guid}/data", (Guid id) => Results.Ok(new
{
    presets = userData.PresetsOf(id).User,
    settings = userData.SettingsOf(id),
    jobs = queue.Jobs.Count(j => j.Owner == id),
    waiting = queue.Jobs.Count(j => j.Owner == id && j.Status is JobStatus.Ready or JobStatus.Running or JobStatus.Paused),
})).AdminOnly();
app.MapDelete("/api/accounts/{id:guid}/presets/{name}", (Guid id, string name) =>
{
    var ok = userData.PresetsOf(id).Delete(name);
    PresetsChanged();
    return ok ? Results.Ok() : Results.NotFound();
}).AdminOnly();
app.MapPost("/api/accounts/{id:guid}/presets/{name}/share", (Guid id, string name) =>
{
    var p = userData.PresetsOf(id).User.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException(name);
    var saved = queue.Presets.Save(p);
    PresetsChanged();
    return Results.Ok(saved);
}).AdminOnly();
app.MapDelete("/api/accounts/{id:guid}/settings", (Guid id) => { userData.SaveSettings(id, new UserSettings()); return Results.Ok(); }).AdminOnly();
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
app.MapPut("/api/settings", (SettingsUpdate u, HttpContext ctx) =>
{
    var s = queue.Settings;
    // Users keep their own quick-form defaults and theme; everything else is the administrators' (server-wide).
    var user = ctx.CurrentUser();
    if (user.HasOwnData)
    {
        if (u.Defaults is not null && DenyAdvanced(ctx, u.Defaults) is { } denied) return denied;
        var mine = userData.SettingsOf(user.Id);
        if (u.Defaults is not null) mine = mine with { Defaults = u.Defaults };
        if (u.Theme is not null) mine = mine with { Theme = u.Theme };
        userData.SaveSettings(user.Id, mine);
        return Results.Ok(new { settings = SettingsFor(user), tools = Mapping.Tools(queue), advancedAllowed = false });
    }
    if (!user.IsAdmin) return Results.Problem(L.T("Only an administrator can do this."), statusCode: 403);
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
    if (u.VapourSynthForEncodes is { } vse) s.VapourSynthForEncodes = vse;
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
    var progress = new Progress<string>(line => hub.ToAdmins("detect", new { line }));
    var caps = await queue.DetectHardwareAsync(progress, ct);
    return caps is null ? Results.Problem(L.T("FFmpeg was not found. Configure it in Settings."), statusCode: 409) : Results.Ok(caps);
}).AdminOnly();

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
}).AdminOnly();

app.MapGet("/api/components", async (CancellationToken ct) =>
{
    try { return Results.Ok(await queue.Components.CheckAsync(queue.Tools, online: true, ct)); }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Results.Ok(await queue.Components.CheckAsync(queue.Tools, online: false, ct));
    }
}).AdminOnly();

app.MapPost("/api/components/{id}/install", async (string id, CancellationToken ct) =>
{
    if (componentsLocked) return Results.Problem(L.T("Component installation is disabled on this server (KNA_LOCK_COMPONENTS)."), statusCode: 403);
    bool isModel = id.StartsWith("model:", StringComparison.Ordinal) || id.StartsWith("llm:", StringComparison.Ordinal);
    if (queue.IsRunning && !isModel) return Results.Problem(L.T("Stop the queue before updating components."), statusCode: 409);
    var progress = new Progress<double>(p => hub.ToAdmins("component", new { id, percent = Math.Round(p, 1) }));
    var logger = new Progress<string>(l => hub.ToAdmins("component", new { id, line = l }));
    try
    {
        await queue.Components.InstallAsync(id, progress, logger, ct);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or PlatformNotSupportedException)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
    queue.RefreshTools();
    if (id == "ffmpeg") await queue.DetectHardwareAsync(new Progress<string>(l => hub.ToAdmins("detect", new { line = l })), ct);
    return Results.Ok(await queue.Components.CheckAsync(queue.Tools, online: false, ct));
}).AdminOnly();

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
    if (Auth.Disabled && browser.Roots.Any(r => r == Path.GetPathRoot(r)))
        log.LogWarning("The whole file system is reachable from the browser and accounts are switched off (KNA_AUTH=off): set KNA_MEDIA_ROOTS.");
    if (accounts.SetupRequired && !Auth.Disabled)
        log.LogWarning("No account yet: the first person who opens {Url} creates the administrator.", app.Urls.FirstOrDefault());
    if (queue.HardwareTestNeeded() is { } why)
    {
        log.LogInformation("Detecting hardware encoders ({Why})…", why);
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
    var browser = B(ctx);
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

static int ResetPassword(string name)
{
    var dataStore = new SettingsStore();
    var pw = AppEnv.Get("NEW_PASSWORD");
    if (pw is null)
    {
        Console.Write($"New password for \"{name}\": ");
        pw = ReadSecret();
        Console.Write("Again: ");
        if (ReadSecret() != pw) { Console.Error.WriteLine("The two passwords differ."); return 1; }
    }
    try
    {
        var accountStore = new AccountStore(dataStore.DataDirectory);
        var a = accountStore.ResetPassword(name, pw ?? "");
        // Run as root (sudo, docker exec): give the files back to the owner of the data folder, the service user.
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            try
            {
                using var chown = Process.Start(new ProcessStartInfo("chown", ["--reference=" + dataStore.DataDirectory, accountStore.AccountsPath, accountStore.SessionsPath])
                    { RedirectStandardError = true });
                chown?.WaitForExit(10_000);
            }
            catch (System.ComponentModel.Win32Exception) { }
        Console.WriteLine($"Password of \"{a.Name}\" ({a.Role}) set; its sessions were signed out. Data folder: {dataStore.DataDirectory}");
        return 0;
    }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }

    static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
    }
}

static bool IsContainer() => File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

static string Platform() =>
    (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux") + (IsContainer() ? " (container)" : "")
    + " · " + KnA.Core.Tools.CpuFeatures.Description;

public partial class Program;
