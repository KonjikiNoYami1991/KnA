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
        var venvPy = Path.Combine(dataDirectory, "ai", "venv", OperatingSystem.IsWindows() ? "Scripts" : "bin", FfmpegLocator.ExeName("python"));
        bool venv = File.Exists(venvPy);
        var sysPy = FfmpegLocator.LocateTool(["python3", "python"], dataDirectory);
        list.Add(new ComponentStatus("ai-runtime", L.T("AI upscaler (Python + PyTorch)"),
            L.T("Private Python environment with PyTorch ({0}) and spandrel for the neural models. Several GB.", TorchFlavor().Label),
            venv && state.TryGetValue("ai-runtime", out var rv) ? rv.Version : null, venv ? venvPy : null, venv, null, false, sysPy is not null || venv,
            sysPy is null && !venv ? L.T("Install Python 3.10+ first (python.org, your package manager, or the Docker image).") : null));

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
            else if (id == "ai-runtime") await InstallAiRuntimeAsync(log, ct);
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

    /// <summary>Which PyTorch build fits this machine: CUDA (NVIDIA, incl. RTX 50 / Blackwell), ROCm (AMD, Linux), default (Apple MPS / CPU).</summary>
    public static (string Label, string? IndexUrl) TorchFlavor()
    {
        if (OperatingSystem.IsMacOS()) return ("Apple MPS", null);
        bool nvidia = FfmpegLocator.LocateTool(["nvidia-smi"], Path.GetTempPath()) is not null || File.Exists("/proc/driver/nvidia/version");
        if (nvidia) return ("CUDA 12.8", "https://download.pytorch.org/whl/cu128");
        if (OperatingSystem.IsLinux() && (Directory.Exists("/opt/rocm") || File.Exists("/dev/kfd"))) return ("ROCm 6.4", "https://download.pytorch.org/whl/rocm6.4");
        return ("CPU", OperatingSystem.IsLinux() || OperatingSystem.IsWindows() ? "https://download.pytorch.org/whl/cpu" : null);
    }

    private async Task InstallAiRuntimeAsync(IProgress<string>? log, CancellationToken ct)
    {
        var py = FfmpegLocator.LocateTool(["python3", "python"], dataDirectory)
                 ?? throw new InvalidOperationException(L.T("Install Python 3.10+ first (python.org, your package manager, or the Docker image)."));
        var venv = Path.Combine(dataDirectory, "ai", "venv");
        var vpy = Path.Combine(venv, OperatingSystem.IsWindows() ? "Scripts" : "bin", FfmpegLocator.ExeName("python"));
        async Task Run(string exe, string[] args, int minutes)
        {
            log?.Report("> " + Path.GetFileName(exe) + " " + string.Join(' ', args));
            var r = await ProcessRunner.RunAsync(exe, args, timeout: TimeSpan.FromMinutes(minutes), ct: ct);
            foreach (var line in (r.StdOut + r.StdErr).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(5)) log?.Report(line.Trim());
            if (r.ExitCode != 0) throw new InvalidOperationException(L.T("{0} failed: {1}", Path.GetFileName(exe), (r.StdErr + r.StdOut).Trim().Split('\n').LastOrDefault()));
        }
        if (!File.Exists(vpy)) await Run(py, ["-m", "venv", venv], 5);
        await Run(vpy, ["-m", "pip", "install", "--upgrade", "pip"], 10);
        var (label, index) = TorchFlavor();
        log?.Report(L.T("Installing PyTorch ({0}): this can take a while…", label));
        try { await Run(vpy, index is null ? ["-m", "pip", "install", "torch"] : ["-m", "pip", "install", "torch", "--index-url", index], 60); }
        catch (InvalidOperationException) when (index is not null)
        {
            log?.Report(L.T("The PyTorch index is not reachable: trying PyPI."));
            await Run(vpy, ["-m", "pip", "install", "torch"], 60);
        }
        await Run(vpy, ["-m", "pip", "install", "spandrel", "numpy"], 20);
        var check = await ProcessRunner.RunAsync(vpy, ["-c", "import torch, spandrel; print(torch.__version__)"], timeout: TimeSpan.FromMinutes(2), ct: ct);
        if (check.ExitCode != 0) throw new InvalidOperationException(check.StdErr.Trim());
        SaveState("ai-runtime", "torch " + check.StdOut.Trim() + " · " + label);
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
