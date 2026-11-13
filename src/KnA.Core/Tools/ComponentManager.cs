using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using KnA.Core.Localization;
using KnA.Core.Processing;

namespace KnA.Core.Tools;

public sealed record ComponentStatus(
    string Id,
    string Name,
    string Description,
    string? InstalledVersion,
    string? InstalledPath,
    bool ManagedByKnA,
    string? LatestVersion,
    bool UpdateAvailable,
    bool CanInstall,
    string? Note);

/// <summary>
/// Keeps the external programs up to date:
///  • FFmpeg + FFprobe: BtbN static "gpl" builds (Windows .zip, Linux .tar.xz) of the NEWEST release branch
///    (e.g. 9.0) or, on request, of master; version = branch + build date;
///  • fdkaac: standalone Fraunhofer AAC encoder built by KnA's own release workflow (every platform);
///    on macOS Homebrew is the supported source (no verified static build is downloaded);
///  • dovi_tool and hdr10plus_tool (quietvoid) for Dolby Vision / HDR10+ passthrough: every platform.
/// Installed copies live in the data folder and take precedence over the system ones.
/// </summary>
public sealed class ComponentManager(string dataDirectory)
{
    /// <summary>"release" = newest FFmpeg release branch (default), "master" = nightly master.</summary>
    public string FfmpegChannel { get; set; } = "release";

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
                    ffLatest = FfmpegVersionLabel(PickFfmpegAsset(rel.Assets, FfmpegChannel)?.Name ?? "", rel.PublishedAt);
                    ffUpdate = !ffManaged || !state.TryGetValue("ffmpeg", out var i) || i.Version != ffLatest;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { ffNote = ex.Message; }
        }
        list.Add(new ComponentStatus("ffmpeg", "FFmpeg + FFprobe", L.T("Encoding, decoding, filters, subtitles (libass)."),
            ffManaged && state.TryGetValue("ffmpeg", out var fi) ? $"{fi.Version} (BtbN)" : Short(ffVersion),
            tools?.Ffmpeg, ffManaged, ffLatest, ffUpdate && ffCan, ffCan, ffNote));

        // ---------------------------------------------------------------- fdkaac (KnA release assets)
        {
            var path = tools?.Fdkaac;
            bool managed = path?.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase) ?? false;
            string? latest = null, note = path is null ? L.T("Not installed: AAC uses FFmpeg's own encoder (lower quality).") : null;
            if (online)
            {
                try { latest = (await GitHubReleases.LatestAsync(AppUpdater.Repository, ct))?.Assets.Any(a => a.Name == FdkaacAssetName()) == true ? "KnA" : null; }
                catch (Exception ex) when (ex is not OperationCanceledException) { note = ex.Message; }
            }
            string? installed = path is null ? null : state.TryGetValue("fdkaac", out var fk) ? fk.Version : "1.x";
            list.Add(new ComponentStatus("fdkaac", "fdkaac (Fraunhofer AAC)",
                L.T("Best-quality AAC / HE-AAC encoder, used automatically when FFmpeg has no libfdk_aac."),
                installed, path, managed, latest, latest is not null && path is null, FdkaacAssetName() is not null, note));
        }

        // ---------------------------------------------------------------- upscaling shaders and the AI runtime
        var shaders = Ai.Upscalers.ShaderDir(dataDirectory);
        bool a4k = File.Exists(Path.Combine(shaders, "kna-anime4k-A.glsl"));
        list.Add(new ComponentStatus("anime4k", "Anime4K shaders", L.T("CNN upscaling shaders for anime (bloc97/Anime4K, MIT). Run on the GPU through libplacebo."),
            a4k && state.TryGetValue("anime4k", out var av) ? av.Version : null, a4k ? shaders : null, a4k, null, false, true, null));
        bool fsr = File.Exists(Path.Combine(shaders, Ai.Upscalers.FsrcnnxShader));
        list.Add(new ComponentStatus("fsrcnnx", "FSRCNNX shader", L.T("Neural ×2 upscaling shader (igv/FSRCNN-TensorFlow). Runs on the GPU through libplacebo."),
            fsr && state.TryGetValue("fsrcnnx", out var fv) ? fv.Version : null, fsr ? shaders : null, fsr, null, false, true, null));
        {
            var venvPy = VenvPython(dataDirectory);
            bool venv = File.Exists(venvPy);
            string? installed = venv && state.TryGetValue("ai-runtime", out var rv) ? rv.Version : venv ? "?" : null;
            string? latest = null, note = null;
            bool portable = PortablePythonTriple() is not null;
            if (online)
            {
                latest = await LatestTorchAsync(ct);
                if (latest is not null) latest = "torch " + latest;
            }
            var installedTorch = installed is null ? null : System.Text.RegularExpressions.Regex.Match(installed, @"torch (\d+(\.\d+)+)") is { Success: true } m ? m.Groups[1].Value : null;
            bool update = venv && latest is not null && (installedTorch is null || ReleaseInfo.ParseVersion(latest[6..]) > ReleaseInfo.ParseVersion(installedTorch));
            if (!venv && !portable && FfmpegLocator.LocateTool(["python3", "python"], dataDirectory) is null)
                note = L.T("Install Python 3.11–3.14 first (python.org, your package manager, or the Docker image).");
            else if (!venv)
                note = portable ? L.T("Python is not needed: KnA downloads a portable copy when the system has none (3.11–3.14).") : null;
            list.Add(new ComponentStatus("ai-runtime", L.T("AI upscaler (Python + PyTorch)"),
                L.T("Private Python environment with PyTorch ({0}) and spandrel for the neural models. Several GB.", (await TorchFlavorAsync(ct, online: false)).Label),
                installed, venv ? venvPy : null, venv, latest, update, true, note));
        }

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
            else if (id == "fdkaac") await InstallFdkaacAsync(temp, progress, log, ct);
            else if (id == "anime4k") await InstallAnime4KAsync(temp, progress, log, ct);
            else if (id == "fsrcnnx") await InstallFsrcnnxAsync(progress, log, ct);
            else if (id == "ai-runtime") await InstallAiRuntimeAsync(temp, progress, log, ct);
            else if (id is "dovi_tool" or "hdr10plus_tool") await InstallQuietvoidAsync(id, temp, progress, log, ct);
            else throw new ArgumentException("Unknown component " + id);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- FFmpeg

    /// <summary>BtbN target name for this machine (win64, winarm64, linux64, linuxarm64), null elsewhere.</summary>
    public static string? FfmpegTarget() => (OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), Arch) switch
    {
        (true, _, "x64") => "win64",
        (true, _, "arm64") => "winarm64",
        (_, true, "x64") => "linux64",
        (_, true, "arm64") => "linuxarm64",
        _ => null,
    };

    /// <summary>Master build asset name (also the fallback when no release-branch build is listed).</summary>
    public static string? FfmpegAssetName() => FfmpegTarget() is { } t
        ? $"ffmpeg-master-latest-{t}-gpl.{(t.StartsWith("win", StringComparison.Ordinal) ? "zip" : "tar.xz")}" : null;

    /// <summary>
    /// Picks the asset of the newest release branch ("ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz") from the
    /// BtbN "latest" release, or the master build when asked / when none is listed.
    /// </summary>
    public static ReleaseAsset? PickFfmpegAsset(IReadOnlyList<ReleaseAsset> assets, string channel)
    {
        if (FfmpegTarget() is not { } t) return null;
        if (channel != "master")
        {
            var rx = new System.Text.RegularExpressions.Regex($@"^ffmpeg-n(\d+)\.(\d+)-latest-{t}-gpl-\1\.\2\.(zip|tar\.xz)$");
            var best = assets.Select(a => (a, m: rx.Match(a.Name))).Where(x => x.m.Success)
                .OrderByDescending(x => int.Parse(x.m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .ThenByDescending(x => int.Parse(x.m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(x => x.a).FirstOrDefault();
            if (best is not null) return best;
        }
        return assets.FirstOrDefault(a => a.Name == FfmpegAssetName());
    }

    /// <summary>fdkaac asset published by KnA's release workflow for this machine.</summary>
    public static string? FdkaacAssetName() => (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), Arch) switch
    {
        (true, _, _, "x64") => "fdkaac-win-x64.zip",
        (_, true, _, _) => "fdkaac-osx.zip",
        (_, _, true, "x64") => "fdkaac-linux-x64.tar.gz",
        (_, _, true, "arm64") => "fdkaac-linux-arm64.tar.gz",
        _ => null,
    };

    private async Task InstallFdkaacAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var name = FdkaacAssetName() ?? throw new PlatformNotSupportedException();
        var rel = await GitHubReleases.LatestAsync(AppUpdater.Repository, ct) ?? throw new InvalidOperationException(L.T("No release found for {0}.", "KnA"));
        var asset = rel.Assets.FirstOrDefault(a => a.Name == name)
                    ?? throw new InvalidOperationException(L.T("No download for this platform in {0} {1}.", "KnA", rel.Tag));
        var archive = Path.Combine(temp, asset.Name);
        log?.Report(L.T("Downloading {0}…", asset.Name));
        await GitHubReleases.DownloadAsync(asset.Url, archive, progress, ct);
        var extracted = Path.Combine(temp, "x");
        await ExtractAsync(archive, extracted, ct);
        var dir = FfmpegLocator.ManagedDir(dataDirectory, "fdkaac");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories))
            if (Path.GetFileName(f) is var n && (n == FfmpegLocator.ExeName("fdkaac") || n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                ReplaceFile(f, Path.Combine(dir, n));
        var exe = Path.Combine(dir, FfmpegLocator.ExeName("fdkaac"));
        if (!File.Exists(exe)) throw new InvalidDataException(L.T("The archive did not contain {0}.", "fdkaac"));
        var r = await ProcessRunner.RunAsync(exe, ["--help"], timeout: TimeSpan.FromSeconds(15), ct: ct);
        if (!(r.StdOut + r.StdErr).Contains("fdkaac", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("The installed program does not start: {0}", r.StdErr.Trim()));
        SaveState("fdkaac", rel.Tag);
    }

    private async Task InstallFfmpegAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var fallback = FfmpegAssetName() ?? throw new PlatformNotSupportedException(L.T("On macOS update FFmpeg with Homebrew: brew upgrade ffmpeg"));
        var rel = await GitHubReleases.ByTagAsync(FfmpegRepo, "latest", ct);
        var picked = rel is null ? null : PickFfmpegAsset(rel.Assets, FfmpegChannel);
        var asset = picked?.Name ?? fallback;
        var url = picked?.Url ?? $"https://github.com/{FfmpegRepo}/releases/download/latest/{asset}";
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
        SaveState("ffmpeg", FfmpegVersionLabel(asset, rel?.PublishedAt ?? DateTimeOffset.Now));
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

    // ---------------------------------------------------------------- upscaling

    private async Task InstallAnime4KAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        // The API may be unreachable (rate limits, proxies): fall back to the known v4.0.1 download.
        ReleaseInfo? rel = null;
        try { rel = await GitHubReleases.LatestAsync("bloc97/Anime4K", ct); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException) { log?.Report(ex.Message); }
        var asset = rel?.Assets.FirstOrDefault(a => a.Name.StartsWith("Anime4K_v", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    ?? new ReleaseAsset("Anime4K_v4.0.zip", "https://github.com/bloc97/Anime4K/releases/download/v4.0.1/Anime4K_v4.0.zip", 0);
        rel ??= new ReleaseInfo("v4.0.1", "Anime4K", null, "", DateTimeOffset.Now, [asset]);
        var archive = Path.Combine(temp, asset.Name);
        log?.Report(L.T("Downloading {0}…", asset.Name));
        await GitHubReleases.DownloadAsync(asset.Url, archive, progress, ct);
        var dir = Ai.Upscalers.ShaderDir(dataDirectory);
        var extracted = Path.Combine(temp, "x");
        await ExtractAsync(archive, extracted, ct);
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(extracted, "Anime4K_*.glsl", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
        Ai.Upscalers.BuildAnime4KChains(dir);
        if (!File.Exists(Path.Combine(dir, "kna-anime4k-A.glsl"))) throw new InvalidDataException(L.T("The archive did not contain {0}.", "Anime4K mode A"));
        SaveState("anime4k", rel.Tag);
    }

    private async Task InstallFsrcnnxAsync(IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        ReleaseInfo? rel = null;
        try { rel = await GitHubReleases.LatestAsync("igv/FSRCNN-TensorFlow", ct); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException) { log?.Report(ex.Message); }
        var asset = rel?.Assets.FirstOrDefault(a => a.Name == Ai.Upscalers.FsrcnnxShader)
                    ?? new ReleaseAsset(Ai.Upscalers.FsrcnnxShader, "https://github.com/igv/FSRCNN-TensorFlow/releases/latest/download/" + Ai.Upscalers.FsrcnnxShader, 0);
        rel ??= new ReleaseInfo("latest", "FSRCNNX", null, "", DateTimeOffset.Now, [asset]);
        var dir = Ai.Upscalers.ShaderDir(dataDirectory);
        Directory.CreateDirectory(dir);
        log?.Report(L.T("Downloading {0}…", asset.Name));
        await GitHubReleases.DownloadAsync(asset.Url, Path.Combine(dir, Ai.Upscalers.FsrcnnxShader), progress, ct);
        SaveState("fsrcnnx", rel.Tag);
    }

    // ---------------------------------------------------------------- AI runtime (Python + PyTorch)

    private const string PythonRepo = "astral-sh/python-build-standalone";
    /// <summary>Python minor versions KnA accepts for PyTorch, best first (portable copy: the first one found).</summary>
    private static readonly int[] PythonMinors = [12, 13, 11, 14];

    /// <summary>Target triple of the portable Python builds (python-build-standalone) for this machine, null when there is none.</summary>
    public static string? PortablePythonTriple() => (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(), Arch) switch
    {
        (true, _, _, "x64") => "x86_64-pc-windows-msvc",
        (true, _, _, "arm64") => "aarch64-pc-windows-msvc",
        (_, true, _, "arm64") => "aarch64-apple-darwin",
        (_, true, _, "x64") => "x86_64-apple-darwin",
        (_, _, true, "x64") => "x86_64-unknown-linux-gnu",
        (_, _, true, "arm64") => "aarch64-unknown-linux-gnu",
        _ => null,
    };

    /// <summary>KnA's own portable Python (data folder), whether installed or not.</summary>
    public static string PortablePython(string dataDirectory) => OperatingSystem.IsWindows()
        ? Path.Combine(dataDirectory, "ai", "python", "python.exe")
        : Path.Combine(dataDirectory, "ai", "python", "bin", "python3");

    public static string VenvPython(string dataDirectory) =>
        Path.Combine(dataDirectory, "ai", "venv", OperatingSystem.IsWindows() ? "Scripts" : "bin", FfmpegLocator.ExeName("python"));

    /// <summary>
    /// Picks "cpython-3.12.N+TAG-triple-install_only.tar.gz" (newest patch of the preferred minor) from release file names.
    /// </summary>
    public static string? PickPortablePython(IEnumerable<string> names, string triple)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            $@"^cpython-3\.(\d+)\.(\d+)\+\d+-{System.Text.RegularExpressions.Regex.Escape(triple)}-install_only\.tar\.gz$");
        var found = names.Select(n => (n, m: rx.Match(n))).Where(x => x.m.Success)
            .Select(x => (x.n, minor: int.Parse(x.m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                patch: int.Parse(x.m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToList();
        foreach (var minor in PythonMinors)
            if (found.Where(x => x.minor == minor).OrderByDescending(x => x.patch).FirstOrDefault() is { n: { } name }) return name;
        return null;
    }

    /// <summary>"3.12" of a Python interpreter, null when it does not start (e.g. the Windows Store stub).</summary>
    public static async Task<Version?> PythonVersionAsync(string python, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() && python.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var r = await ProcessRunner.RunAsync(python, ["-c", "import sys; print('%d.%d' % sys.version_info[:2])"], timeout: TimeSpan.FromSeconds(20), ct: ct);
            return r.ExitCode == 0 && Version.TryParse(r.StdOut.Trim(), out var v) ? v : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private static bool Suitable(Version? v) => v is { Major: 3 } && PythonMinors.Contains(v.Minor);

    /// <summary>Downloads the portable Python (python-build-standalone, with pip and venv) into the data folder.</summary>
    private async Task<string> InstallPortablePythonAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var triple = PortablePythonTriple() ?? throw new PlatformNotSupportedException(L.T("Install Python 3.11–3.14 first (python.org, your package manager, or the Docker image)."));
        string? tag = null, name = null, sha = null;
        // The release has hundreds of files: the API may not list them all, SHA256SUMS always does (and gives the checksum).
        try
        {
            var rel = await GitHubReleases.LatestAsync(PythonRepo, ct);
            tag = rel?.Tag;
            if (rel is not null) name = PickPortablePython(rel.Assets.Select(a => a.Name), triple);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException) { log?.Report(ex.Message); }
        if (tag is null)
        {
            using var latest = await GitHubReleases.Client.GetAsync($"https://raw.githubusercontent.com/{PythonRepo}/latest-release/latest-release.json", ct);
            latest.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(ct));
            tag = doc.RootElement.TryGetProperty("tag", out var t) ? t.ToString() : throw new InvalidOperationException(L.T("No release found for {0}.", "Python"));
        }
        var baseUrl = $"https://github.com/{PythonRepo}/releases/download/{tag}/";
        try
        {
            var sums = await GitHubReleases.Client.GetStringAsync(baseUrl + "SHA256SUMS", ct);
            var lines = sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)).Where(x => x.Length == 2)
                .ToDictionary(x => x[1].TrimStart('*'), x => x[0], StringComparer.Ordinal);
            name ??= PickPortablePython(lines.Keys, triple);
            if (name is not null) sha = lines.GetValueOrDefault(name);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { log?.Report(ex.Message); }
        if (name is null) throw new InvalidOperationException(L.T("No download for this platform in {0} {1}.", "python-build-standalone", tag));

        var archive = Path.Combine(temp, name);
        log?.Report(L.T("Downloading {0}…", name));
        await GitHubReleases.DownloadAsync(baseUrl + Uri.EscapeDataString(name), archive, progress, ct);
        if (sha is not null)
        {
            await using var fs = File.OpenRead(archive);
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct));
            if (!hash.Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("Checksum mismatch for {0}.", name));
        }
        var extracted = Path.Combine(temp, "py");
        await ExtractAsync(archive, extracted, ct);
        var root = Path.Combine(extracted, "python");
        if (!Directory.Exists(root)) throw new InvalidDataException(L.T("The archive did not contain {0}.", "python"));
        var target = Path.Combine(dataDirectory, "ai", "python");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(root, target);
        var exe = PortablePython(dataDirectory);
        if (!OperatingSystem.IsWindows() && File.Exists(exe))
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        var v = await PythonVersionAsync(exe, ct) ?? throw new InvalidOperationException(L.T("The installed program does not start: {0}", exe));
        log?.Report(L.T("Portable Python {0} ready.", v));
        return exe;
    }

    private static readonly string[] FallbackCuda = ["cu130", "cu128", "cu126"];
    private static readonly string[] FallbackRocm = ["rocm7.0", "rocm6.4"];
    private const string TorchIndex = "https://download.pytorch.org/whl/";

    /// <summary>
    /// Which PyTorch build fits this machine: CUDA (NVIDIA, the newest the driver supports; RTX 50 / Blackwell need 12.8+),
    /// ROCm (AMD, Linux), default PyPI build (Apple MPS) or CPU. IndexUrl = pip index, null = PyPI.
    /// Online, the CUDA/ROCm builds of the newest PyTorch are read from download.pytorch.org; offline a known list is used.
    /// </summary>
    public static async Task<(string Label, string? IndexUrl)> TorchFlavorAsync(CancellationToken ct, bool online = true)
    {
        if (OperatingSystem.IsMacOS()) return ("Apple MPS", null);
        var smi = FfmpegLocator.LocateTool(["nvidia-smi"], Path.GetTempPath());
        bool nvidia = smi is not null || File.Exists("/proc/driver/nvidia/version");
        if (nvidia)
        {
            // "CUDA Version: 12.8" in nvidia-smi's header is the newest CUDA runtime the driver can run.
            double? driverCuda = null;
            if (smi is not null)
            {
                try
                {
                    var r = await ProcessRunner.RunAsync(smi, [], timeout: TimeSpan.FromSeconds(15), ct: ct);
                    var m = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"CUDA Version:\s*(\d+)\.(\d+)");
                    if (m.Success) driverCuda = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) / 10.0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
            var tags = (online ? await TorchTagsAsync("cu", ct) : null) ?? [.. FallbackCuda];
            var fit = tags.Select(t => (t, v: CudaOf(t))).Where(x => driverCuda is null || x.v <= driverCuda + 1e-6)
                .OrderByDescending(x => x.v).Select(x => x.t).FirstOrDefault() ?? tags.OrderBy(CudaOf).First();
            return ($"CUDA {CudaOf(fit):0.0}", TorchIndex + fit);
        }
        if (OperatingSystem.IsLinux() && (Directory.Exists("/opt/rocm") || File.Exists("/dev/kfd")))
        {
            var tags = (online ? await TorchTagsAsync("rocm", ct) : null) ?? [.. FallbackRocm];
            var best = tags.OrderByDescending(t => Version.TryParse(t[4..], out var v) ? v : new Version(0, 0)).First();
            return ("ROCm " + best[4..], TorchIndex + best);
        }
        return ("CPU", OperatingSystem.IsLinux() || OperatingSystem.IsWindows() ? TorchIndex + "cpu" : null);
    }

    public static double CudaOf(string tag) =>
        int.TryParse(tag.AsSpan(2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n / 10 + n % 10 / 10.0 : 0;

    /// <summary>"cu130"/"rocm6.4" builds of the newest PyTorch on download.pytorch.org for this platform (null when unreachable).</summary>
    private static async Task<List<string>?> TorchTagsAsync(string kind, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            var html = await GitHubReleases.Client.GetStringAsync(TorchIndex + "torch/", cts.Token);
            var plat = OperatingSystem.IsWindows() ? (Arch == "arm64" ? "win_arm64" : "win_amd64") : Arch == "arm64" ? "aarch64" : "x86_64";
            var tags = ParseTorchTags(html, kind, plat);
            return tags.Count == 0 ? null : tags;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException) { return null; }
    }

    /// <summary>Builds ("cu130", "rocm6.4"…) of the newest PyTorch listed in the download.pytorch.org index page for a platform.</summary>
    public static List<string> ParseTorchTags(string html, string kind, string platform)
    {
        var rx = new System.Text.RegularExpressions.Regex($@"torch-(\d+\.\d+\.\d+)(?:%2B|\+)({kind}\d+(?:\.\d+)*)-cp3\d+-cp3\d+t?-[^""<#]*{platform}[^""<#]*\.whl");
        var all = rx.Matches(html).Select(m => (v: Version.Parse(m.Groups[1].Value), tag: m.Groups[2].Value)).ToList();
        if (all.Count == 0) return [];
        var newest = all.Max(x => x.v)!;
        return [.. all.Where(x => x.v == newest).Select(x => x.tag).Distinct()];
    }

    /// <summary>Newest PyTorch on PyPI (null when offline).</summary>
    public static async Task<string?> LatestTorchAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await GitHubReleases.Client.GetStringAsync("https://pypi.org/pypi/torch/json", ct));
            return doc.RootElement.GetProperty("info").GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Private AI environment: KnA's portable Python (downloaded when no suitable 3.11–3.14 is installed, or always
    /// when it is already there) → venv in the data folder → PyTorch for the GPU found → spandrel + numpy.
    /// Running it again upgrades everything (and rebuilds the venv when its Python is too old for current PyTorch).
    /// </summary>
    private async Task InstallAiRuntimeAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var venv = Path.Combine(dataDirectory, "ai", "venv");
        var vpy = VenvPython(dataDirectory);
        async Task Run(string exe, string[] args, int minutes)
        {
            log?.Report("> " + Path.GetFileName(exe) + " " + string.Join(' ', args));
            var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromMinutes(minutes), ct: ct);
            foreach (var line in (r.StdOut + r.StdErr).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(5)) log?.Report(line.Trim());
            if (r.ExitCode != 0) throw new InvalidOperationException(L.T("{0} failed: {1}", Path.GetFileName(exe), (r.StdErr + r.StdOut).Trim().Split('\n').LastOrDefault()));
        }

        // 1. Python: the portable copy, else a suitable system one, else download the portable copy.
        string? basePy = File.Exists(PortablePython(dataDirectory)) && Suitable(await PythonVersionAsync(PortablePython(dataDirectory), ct)) ? PortablePython(dataDirectory) : null;
        if (basePy is null)
        {
            foreach (var candidate in new[] { FfmpegLocator.LocateTool(["python3"], dataDirectory), FfmpegLocator.LocateTool(["python"], dataDirectory) }.OfType<string>().Distinct())
            {
                var v = await PythonVersionAsync(candidate, ct);
                if (Suitable(v)) { basePy = candidate; log?.Report(L.T("Using the system Python {0}: {1}", v, candidate)); break; }
                if (v is not null) log?.Report(L.T("Python {0} ({1}) is not supported by current PyTorch: KnA downloads its own.", v, candidate));
            }
        }
        if (basePy is null && PortablePythonTriple() is not null)
        {
            log?.Report(L.T("Downloading a portable Python (no installation, stays in KnA's data folder)…"));
            basePy = await InstallPortablePythonAsync(temp, progress, log, ct);
        }
        if (basePy is null) throw new InvalidOperationException(L.T("Install Python 3.11–3.14 first (python.org, your package manager, or the Docker image)."));

        // 2. venv (rebuilt when its Python is missing or too old).
        if (File.Exists(vpy) && !Suitable(await PythonVersionAsync(vpy, ct)))
        {
            log?.Report(L.T("The AI environment uses an unsupported Python: rebuilding it."));
            Directory.Delete(venv, true);
        }
        if (!File.Exists(vpy)) await Run(basePy, ["-m", "venv", venv], 10);
        await Run(vpy, ["-m", "pip", "install", "--upgrade", "pip"], 10);

        // 3. PyTorch for this GPU (newest CUDA the driver supports), falling back to PyPI.
        var (label, index) = await TorchFlavorAsync(ct);
        log?.Report(L.T("Installing PyTorch ({0}): this can take a while…", label));
        try { await Run(vpy, index is null ? ["-m", "pip", "install", "--upgrade", "torch"] : ["-m", "pip", "install", "--upgrade", "torch", "--index-url", index], 90); }
        catch (InvalidOperationException) when (index is not null)
        {
            log?.Report(L.T("The PyTorch index is not reachable: trying PyPI."));
            await Run(vpy, ["-m", "pip", "install", "--upgrade", "torch"], 90);
        }
        await Run(vpy, ["-m", "pip", "install", "--upgrade", "spandrel", "numpy"], 20);

        // 4. Check: version and whether the GPU is really usable.
        var check = await ProcessRunner.RunAsync(vpy, ["-c",
            "import sys, torch, spandrel\n" +
            "gpu = 'cuda' if torch.cuda.is_available() else ('mps' if getattr(torch.backends, 'mps', None) and torch.backends.mps.is_available() else 'cpu')\n" +
            "name = torch.cuda.get_device_name(0) if gpu == 'cuda' else ''\n" +
            "print(torch.__version__, gpu, '%d.%d.%d' % sys.version_info[:3], name, sep='|')"], timeout: TimeSpan.FromMinutes(3), ct: ct);
        if (check.ExitCode != 0) throw new InvalidOperationException(check.StdErr.Trim());
        var parts = check.StdOut.Trim().Split('|');
        string torch = parts[0], device = parts.ElementAtOrDefault(1) ?? "cpu", pyv = parts.ElementAtOrDefault(2) ?? "", gpuName = parts.ElementAtOrDefault(3) ?? "";
        log?.Report(L.T("PyTorch {0} on {1}{2} · Python {3}", torch, device.ToUpperInvariant(), gpuName.Length > 0 ? " (" + gpuName + ")" : "", pyv));
        if (label.StartsWith("CUDA", StringComparison.Ordinal) && device != "cuda")
            log?.Report(L.T("PyTorch does not see the NVIDIA GPU: update the driver (RTX 50 needs 570 or newer) and install again."));
        SaveState("ai-runtime", $"torch {torch} · {(device == "cpu" ? "CPU" : label)} · Python {pyv}");
    }

    private static string FfmpegVersionLabel(string asset, DateTimeOffset date)
    {
        var m = System.Text.RegularExpressions.Regex.Match(asset, @"ffmpeg-n(\d+\.\d+)-");
        return (m.Success ? m.Groups[1].Value : "master") + " · " + date.ToString("yyyy-MM-dd");
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
