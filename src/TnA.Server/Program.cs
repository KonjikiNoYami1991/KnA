using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TnA.Core.Encoding;
using TnA.Core.Queue;
using TnA.Core.Settings;
using TnA.Core.Tools;
using TnA.Server;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Serve the UI from next to the executable, whatever the current directory is.
    ContentRootPath = AppContext.BaseDirectory,
});

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) && !args.Any(a => a.StartsWith("--urls")))
    builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("TNA_URLS") ?? "http://0.0.0.0:8585");

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = SettingsStore.Json.PropertyNamingPolicy;
    foreach (var c in SettingsStore.Json.Converters) o.SerializerOptions.Converters.Add(c);
});
builder.Services.AddSingleton(new SettingsStore());
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<FileBrowser>();

var app = builder.Build();
var queue = app.Services.GetRequiredService<QueueService>();
var hub = app.Services.GetRequiredService<EventHub>();
var browser = app.Services.GetRequiredService<FileBrowser>();
var log = app.Logger;

bool advancedAllowed = Environment.GetEnvironmentVariable("TNA_ALLOW_ADVANCED") is "1" or "true" or "yes";
bool powerActionsAllowed = Environment.GetEnvironmentVariable("TNA_ALLOW_POWER_ACTIONS") is "1" or "true" or "yes";

// ---------------------------------------------------------------- optional password (HTTP Basic)
var password = Environment.GetEnvironmentVariable("TNA_PASSWORD");
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
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"TnA\", charset=\"UTF-8\"";
        ctx.Response.StatusCode = 401;
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------- state & events
app.MapGet("/api/state", () => new StateDto(AppInfo.Version, Platform(), queue.Settings, OptionsDto.Create(),
    Mapping.Tools(queue), Mapping.QueueState(queue), queue.Jobs.Select(JobDto.From).ToList(), powerActionsAllowed));

app.MapGet("/api/events", (HttpContext ctx, CancellationToken ct) => hub.StreamAsync(ctx, ct));

// ---------------------------------------------------------------- files
app.MapGet("/api/browse", (string? path) =>
{
    try { return Results.Ok(browser.Browse(path)); }
    catch (UnauthorizedAccessException ex) { return Results.Problem(ex.Message, statusCode: 403); }
    catch (IOException ex) { return Results.Problem(ex.Message, statusCode: 400); }
});

// ---------------------------------------------------------------- jobs
app.MapPost("/api/jobs", (AddJobsRequest req) =>
{
    var denied = req.Paths.Where(p => !browser.IsAllowed(p)).ToList();
    if (denied.Count > 0) return Results.Problem($"Outside the allowed media roots: {denied[0]}", statusCode: 403);
    var added = queue.AddPaths(req.Paths, req.Settings, req.Extensions);
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
    catch (Exception ex) { return Results.Text("Cannot build the command: " + ex.Message); }
});

// ---------------------------------------------------------------- queue control
app.MapPost("/api/queue/start", () =>
{
    try { queue.Start(); return Results.Ok(); }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
});
app.MapPost("/api/queue/stop", async () => { await queue.StopAsync(); return Results.Ok(); });
app.MapPost("/api/queue/pause", () => { queue.Pause(); return Results.Ok(); });
app.MapPost("/api/queue/resume", () => { queue.Resume(); return Results.Ok(); });

// ---------------------------------------------------------------- settings & tools
app.MapPut("/api/settings", (SettingsUpdate u) =>
{
    var s = queue.Settings;
    if (u.OutputDirectory is not null)
    {
        if (u.OutputDirectory.Length > 0 && !browser.IsAllowed(u.OutputDirectory))
            return Results.Problem("Output folder outside the allowed media roots.", statusCode: 403);
        s.OutputDirectory = u.OutputDirectory.Length == 0 ? null : u.OutputDirectory;
    }
    if (u.Defaults is not null) s.Defaults = u.Defaults;
    if (u.PostAction is { } pa) s.PostAction = powerActionsAllowed ? pa : PostQueueAction.Nothing;
    if (u.FolderExtensions is not null) s.FolderExtensions = u.FolderExtensions;
    if (u.Theme is not null) s.Theme = u.Theme;
    if (u.VaapiDevice is not null) s.VaapiDevice = u.VaapiDevice.Length == 0 ? null : u.VaapiDevice;
    if (advancedAllowed)
    {
        if (u.FfmpegPath is not null) s.FfmpegPath = u.FfmpegPath.Length == 0 ? null : u.FfmpegPath;
        if (u.ExtraArguments is not null) s.ExtraArguments = u.ExtraArguments;
    }
    queue.SaveSettings();
    queue.RefreshTools();
    return Results.Ok(new { settings = s, tools = Mapping.Tools(queue), advancedAllowed });
});

app.MapPost("/api/hardware/detect", async (CancellationToken ct) =>
{
    var progress = new Progress<string>(line => hub.Broadcast("detect", new { line }));
    var caps = await queue.DetectHardwareAsync(progress, ct);
    return caps is null ? Results.Problem("FFmpeg not found.", statusCode: 409) : Results.Ok(caps);
});

app.MapPost("/api/ffmpeg/download", async (CancellationToken ct) =>
{
    if (FfmpegDownloader.DownloadUrl is null) return Results.Problem(FfmpegDownloader.ManualInstructions, statusCode: 400);
    var progress = new Progress<double>(p => hub.Broadcast("download", new { percent = Math.Round(p, 1) }));
    await FfmpegDownloader.DownloadAsync(queue.Store.DataDirectory, progress, ct);
    queue.RefreshTools();
    return Results.Ok(Mapping.Tools(queue));
});

app.MapGet("/api/info", () => new { advancedAllowed, powerActionsAllowed, roots = browser.Roots, dataDirectory = queue.Store.DataDirectory });

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
    log.LogInformation("TnA {Version} web UI listening on {Urls}", AppInfo.Version, urls);
    log.LogInformation("Data directory: {Dir}", queue.Store.DataDirectory);
    log.LogInformation("Media roots: {Roots}", string.Join(", ", browser.Roots));
    log.LogInformation("FFmpeg: {Ffmpeg}", queue.Tools?.Ffmpeg ?? "NOT FOUND");
    if (queue.Tools is not null && queue.Settings.Capabilities is null)
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
app.Lifetime.ApplicationStopping.Register(() => queue.StopAsync().Wait(TimeSpan.FromSeconds(10)));

app.Run();

static string Platform() =>
    (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")
    + (File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? " (container)" : "");

public partial class Program;
