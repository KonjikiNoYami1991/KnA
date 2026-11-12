using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using TnA.Core.Localization;
using TnA.Core.Processing;

namespace TnA.Core.Tools;

public sealed record ComponentStatus(
    string Id,
    string Name,
    string Description,
    string? InstalledVersion,
    string? InstalledPath,
    bool ManagedByTnA,
    string? LatestVersion,
    bool UpdateAvailable,
    bool CanInstall,
    string? Note);

/// <summary>
/// Keeps the external programs up to date:
///  • FFmpeg + FFprobe: BtbN static "gpl" builds (Windows .zip, Linux .tar.xz), version = build date;
///    on macOS Homebrew is the supported source (no verified static build is downloaded);
///  • dovi_tool and hdr10plus_tool (quietvoid) for Dolby Vision / HDR10+ passthrough: every platform.
/// Installed copies live in the data folder and take precedence over the system ones.
/// </summary>
public sealed class ComponentManager(string dataDirectory)
{
    private const string FfmpegRepo = "BtbN/FFmpeg-Builds";
    private string StatePath => Path.Combine(dataDirectory, "tools", "components.json");

    private sealed record Installed(string Version, DateTimeOffset InstalledAt);

    public static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public async Task<IReadOnlyList<ComponentStatus>> CheckAsync(FfmpegTools? tools, bool online, CancellationToken ct = default)
    {
        var state = LoadState();
        var list = new List<ComponentStatus>();

        // ---------------------------------------------------------------- FFmpeg
        string? ffVersion = tools is null ? null : await ReadVersionAsync(tools.Ffmpeg, ["-hide_banner", "-version"], ct);
        bool ffManaged = tools?.Ffmpeg.StartsWith(FfmpegLocator.ManagedDir(dataDirectory, "ffmpeg"), StringComparison.OrdinalIgnoreCase) ?? false;
        string? ffLatest = null;
        bool ffUpdate = false;
        string? ffNote = null;
        bool ffCan = FfmpegAssetName() is not null;
        if (!ffCan) ffNote = OperatingSystem.IsMacOS()
            ? L.T("On macOS update FFmpeg with Homebrew: brew upgrade ffmpeg")
            : L.T("Not available for this platform: use your package manager.");
        else if (IsContainer() && !ffManaged)
            ffNote = L.T("Inside Docker FFmpeg comes with the image; installing here overrides it.");
        if (online && ffCan)
        {
            try
            {
                var rel = await GitHubReleases.ByTagAsync(FfmpegRepo, "latest", ct);
                if (rel is not null)
                {
                    ffLatest = rel.PublishedAt.ToString("yyyy-MM-dd");
                    ffUpdate = !ffManaged || !state.TryGetValue("ffmpeg", out var i) || i.Version != ffLatest;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { ffNote = ex.Message; }
        }
        list.Add(new ComponentStatus("ffmpeg", "FFmpeg + FFprobe", L.T("Encoding, decoding, filters, subtitles (libass)."),
            ffManaged && state.TryGetValue("ffmpeg", out var fi) ? $"{fi.Version} (BtbN)" : Short(ffVersion),
            tools?.Ffmpeg, ffManaged, ffLatest, ffUpdate && ffCan, ffCan, ffNote));

        // ---------------------------------------------------------------- quietvoid tools
        foreach (var (id, name, desc, path) in new[]
        {
            ("dovi_tool", "dovi_tool", L.T("Keeps Dolby Vision metadata when re-encoding HEVC."), tools?.DoviTool),
            ("hdr10plus_tool", "hdr10plus_tool", L.T("Keeps HDR10+ dynamic metadata when re-encoding HEVC."), tools?.Hdr10PlusTool),
        })
        {
            string? installed = path is null ? null : await ReadVersionAsync(path, ["--version"], ct);
            string? latest = null, note = null;
            if (online)
            {
                try { latest = (await GitHubReleases.LatestAsync("quietvoid/" + id, ct))?.Tag; }
                catch (Exception ex) when (ex is not OperationCanceledException) { note = ex.Message; }
            }
            var inst = installed?.Split(' ').LastOrDefault();
            bool update = latest is not null && (inst is null || ReleaseInfo.ParseVersion(latest) > ReleaseInfo.ParseVersion(inst));
            list.Add(new ComponentStatus(id, name, desc, inst, path,
                path?.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase) ?? false, latest, update, QuietvoidTarget() is not null, note));
        }
        return list;
    }

    public async Task InstallAsync(string id, IProgress<double>? progress = null, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var temp = Path.Combine(dataDirectory, "temp", "component-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            if (id == "ffmpeg") await InstallFfmpegAsync(temp, progress, log, ct);
            else if (id is "dovi_tool" or "hdr10plus_tool") await InstallQuietvoidAsync(id, temp, progress, log, ct);
            else throw new ArgumentException("Unknown component " + id);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- FFmpeg

    public static string? FfmpegAssetName() => (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), Arch) switch
    {
        (true, _, "x64") => "ffmpeg-master-latest-win64-gpl.zip",
        (true, _, "arm64") => "ffmpeg-master-latest-winarm64-gpl.zip",
        (_, true, "x64") => "ffmpeg-master-latest-linux64-gpl.tar.xz",
        (_, true, "arm64") => "ffmpeg-master-latest-linuxarm64-gpl.tar.xz",
        _ => null,
    };

    private async Task InstallFfmpegAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var asset = FfmpegAssetName() ?? throw new PlatformNotSupportedException(L.T("On macOS update FFmpeg with Homebrew: brew upgrade ffmpeg"));
        var rel = await GitHubReleases.ByTagAsync(FfmpegRepo, "latest", ct);
        var url = rel?.Assets.FirstOrDefault(a => a.Name == asset)?.Url
                  ?? $"https://github.com/{FfmpegRepo}/releases/download/latest/{asset}";
        var archive = Path.Combine(temp, asset);
        log?.Report(L.T("Downloading {0}…", asset));
        await GitHubReleases.DownloadAsync(url, archive, progress, ct);
        var extracted = Path.Combine(temp, "x");
        log?.Report(L.T("Extracting…"));
        await ExtractAsync(archive, extracted, ct);

        var target = FfmpegLocator.ManagedDir(dataDirectory, "ffmpeg");
        Directory.CreateDirectory(target);
        foreach (var exe in new[] { "ffmpeg", "ffprobe" })
        {
            var found = Directory.EnumerateFiles(extracted, FfmpegLocator.ExeName(exe), SearchOption.AllDirectories).FirstOrDefault()
                        ?? throw new InvalidDataException(L.T("The archive did not contain {0}.", exe));
            ReplaceFile(found, Path.Combine(target, FfmpegLocator.ExeName(exe)));
        }
        await VerifyAsync(Path.Combine(target, FfmpegLocator.ExeName("ffmpeg")), ["-hide_banner", "-version"], ct);
        SaveState("ffmpeg", (rel?.PublishedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd"));
    }

    // ---------------------------------------------------------------- quietvoid tools

    public static string? QuietvoidTarget() => (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), Arch) switch
    {
        (true, _, _, "x64") => "x86_64-pc-windows-msvc.zip",
        (true, _, _, "arm64") => "aarch64-pc-windows-msvc.zip",
        (_, true, _, _) => "universal-macOS.zip",
        (_, _, true, "x64") => "x86_64-unknown-linux-musl.tar.gz",
        (_, _, true, "arm64") => "aarch64-unknown-linux-musl.tar.gz",
        _ => null,
    };

    private async Task InstallQuietvoidAsync(string id, string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var suffix = QuietvoidTarget() ?? throw new PlatformNotSupportedException();
        var rel = await GitHubReleases.LatestAsync("quietvoid/" + id, ct) ?? throw new InvalidOperationException(L.T("No release found for {0}.", id));
        var asset = rel.Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(L.T("No download for this platform in {0} {1}.", id, rel.Tag));
        var archive = Path.Combine(temp, asset.Name);
        log?.Report(L.T("Downloading {0}…", asset.Name));
        await GitHubReleases.DownloadAsync(asset.Url, archive, progress, ct);
        var extracted = Path.Combine(temp, "x");
        await ExtractAsync(archive, extracted, ct);
        var found = Directory.EnumerateFiles(extracted, FfmpegLocator.ExeName(id), SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidDataException(L.T("The archive did not contain {0}.", id));
        var target = Path.Combine(FfmpegLocator.ManagedDir(dataDirectory, id), FfmpegLocator.ExeName(id));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        ReplaceFile(found, target);
        await VerifyAsync(target, ["--version"], ct);
        SaveState(id, rel.Tag);
    }

    // ---------------------------------------------------------------- helpers

    public static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
        }
        else if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await using var fs = File.OpenRead(archive);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gz, destination, overwriteFiles: true, ct);
        }
        else if (archive.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
        {
            // .NET has no xz decoder: every Linux/macOS system has tar with xz support.
            var r = await ProcessRunner.RunAsync("tar", ["-xJf", archive, "-C", destination], timeout: TimeSpan.FromMinutes(10), ct: ct);
            if (r.ExitCode != 0) throw new InvalidOperationException("tar: " + r.StdErr);
        }
        else throw new NotSupportedException(archive);
    }

    /// <summary>Replaces a file even when the old one is in use (Windows: rename it out of the way first).</summary>
    public static void ReplaceFile(string source, string target)
    {
        if (File.Exists(target))
        {
            var old = target + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { File.Move(target, old, overwrite: true); } catch (IOException) { File.Delete(target); }
        }
        File.Copy(source, target, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                         | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static async Task VerifyAsync(string exe, string[] args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(L.T("The installed program does not start: {0}", r.StdErr.Trim()));
    }

    private static async Task<string?> ReadVersionAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromSeconds(15), ct: ct);
            return (r.StdOut + r.StdErr).Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static string? Short(string? ffmpegVersionLine) =>
        ffmpegVersionLine?.Replace("ffmpeg version ", "").Split(" Copyright")[0].Trim();

    private static bool IsContainer() =>
        File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    private Dictionary<string, Installed> LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<Dictionary<string, Installed>>(File.ReadAllText(StatePath)) ?? [];
        }
        catch (JsonException) { }
        return [];
    }

    private void SaveState(string id, string version)
    {
        var s = LoadState();
        s[id] = new Installed(version, DateTimeOffset.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(s));
    }
}
