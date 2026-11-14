using System.Text;
using System.Text.RegularExpressions;
using KnA.Core.Localization;
using KnA.Core.Processing;
using KnA.Core.Settings;

namespace KnA.Core.Tools;

/// <summary>
/// Third-party encoders and frameservers:
///  • rigaya's NVEncC (NVIDIA), QSVEncC (Intel), VCEEncC (AMD): Windows x64 (.7z, unpacked with 7-Zip's 7zr)
///    and Linux x64 (.deb, unpacked without installing; their private libraries are found through LD_LIBRARY_PATH);
///  • qaac (Windows): Apple's AAC encoder; Apple's CoreAudioToolbox files are extracted on this computer from the
///    official iTunes x64 installer (the makeportable2 method), never redistributed;
///  • VapourSynth portable (Windows) with BestSource and L-SMASH-Works.
/// </summary>
public sealed partial class ComponentManager
{
    public sealed record RigayaTool(string Id, string Name, string Repo, string Prefix, string Description);

    public static IReadOnlyList<RigayaTool> RigayaTools { get; } =
    [
        new("nvencc", "NVEncC (NVIDIA)", "rigaya/NVEnc", "NVEncC", "NVENC with GPU decoding, GPU filters, parallel encoding on every NVENC engine, Dolby Vision / HDR10+ passthrough."),
        new("qsvencc", "QSVEncC (Intel)", "rigaya/QSVEnc", "QSVEncC", "Intel Quick Sync (iGPU / Arc) with GPU decoding and filters."),
        new("vceencc", "VCEEncC (AMD)", "rigaya/VCEEnc", "VCEEncC", "AMD VCE/VCN (Radeon RX 5000 or newer on Linux) with GPU decoding and filters."),
    ];

    /// <summary>Only 64-bit Windows and Linux x64 builds exist (rigaya publishes no arm64 / macOS builds).</summary>
    public static bool RigayaSupported => Arch == "x64" && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

    /// <summary>Picks the release file for this machine: "NVEncC_9.41_x64.7z" or "nvencc_9.41_amd64.deb".</summary>
    public static string? PickRigayaAsset(IEnumerable<string> names, RigayaTool tool) =>
        OperatingSystem.IsWindows()
            ? names.FirstOrDefault(n => Regex.IsMatch(n, $@"^{tool.Prefix}_[\d.]+_x64\.7z$", RegexOptions.IgnoreCase))
            : PickDeb(names, tool.Id, HostUbuntu());

    /// <summary>
    /// The .deb for this distribution: rigaya builds one per Ubuntu release ("nvencc_8.05_Ubuntu24.04_amd64.deb");
    /// the one matching the host, else the newest Ubuntu (Debian, Mint, Proxmox… share its libraries closely enough).
    /// </summary>
    public static string? PickDeb(IEnumerable<string> names, string id, string? ubuntu)
    {
        var debs = names.Select(n => (Name: n, M: Regex.Match(n, $@"^{Regex.Escape(id)}_[\d.]+(?:_ubuntu(\d+\.\d+))?_amd64\.deb$", RegexOptions.IgnoreCase)))
            .Where(x => x.M.Success).ToList();
        if (ubuntu is not null && debs.FirstOrDefault(x => x.M.Groups[1].Value == ubuntu).Name is { } exact) return exact;
        return debs.OrderByDescending(x => double.TryParse(x.M.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Select(x => x.Name).FirstOrDefault();
    }

    /// <summary>VERSION_ID of an Ubuntu host ("24.04"), or null.</summary>
    private static string? HostUbuntu()
    {
        try
        {
            var lines = File.ReadAllLines("/etc/os-release");
            if (!lines.Any(l => l is "ID=ubuntu" or "ID=\"ubuntu\"")) return null;
            return lines.FirstOrDefault(l => l.StartsWith("VERSION_ID=", StringComparison.Ordinal))?[11..].Trim('"');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<List<ComponentStatus>> CheckExternalAsync(FfmpegTools? tools, bool online, CancellationToken ct)
    {
        var state = LoadState();
        var list = new List<ComponentStatus>();
        foreach (var t in RigayaTools)
        {
            var path = t.Id switch { "nvencc" => tools?.NvencC, "qsvencc" => tools?.QsvencC, _ => tools?.VceencC };
            string? installed = path is null ? null : state.TryGetValue(t.Id, out var v) ? v.Version : (await ReadVersionAsync(path, ["--version"], ct))?.Split(' ').FirstOrDefault(x => Regex.IsMatch(x, @"^\d+\.\d+")) ?? "?";
            string? latest = null, note = RigayaSupported ? null : L.T("Not available for this platform: use the FFmpeg encoders.");
            if (online && RigayaSupported)
            {
                try { latest = (await GitHubReleases.LatestAsync(t.Repo, ct))?.Tag; }
                catch (Exception ex) when (ex is not OperationCanceledException) { note = ex.Message; }
            }
            bool update = latest is not null && (installed is null || ReleaseInfo.ParseVersion(latest) > ReleaseInfo.ParseVersion(installed));
            list.Add(new ComponentStatus(t.Id, t.Name, L.T(t.Description), installed, path, path?.StartsWith(AppPaths.Tools(dataDirectory), StringComparison.OrdinalIgnoreCase) ?? false,
                latest, update, RigayaSupported, note));
        }

        // qaac: Windows only (macOS uses the same Apple encoder through AudioToolbox, Linux uses fdkaac).
        {
            var qdir = Path.Combine(AppPaths.Tools(dataDirectory), "qaac");
            bool exe = File.Exists(Path.Combine(qdir, "qaac64.exe"));
            string? installed = exe && state.TryGetValue("qaac", out var qv) ? qv.Version : null;
            string? latest = null;
            string? note = !OperatingSystem.IsWindows()
                ? L.T("Windows only: on macOS KnA uses the same Apple encoder (AudioToolbox), elsewhere fdkaac.")
                : exe && tools?.Qaac is null ? L.T("Apple's CoreAudioToolbox is missing: install again to extract it from the iTunes installer.") : null;
            if (online && OperatingSystem.IsWindows())
            {
                try { latest = (await GitHubReleases.LatestAsync("nu774/qaac", ct))?.Tag; }
                catch (Exception ex) when (ex is not OperationCanceledException) { note ??= ex.Message; }
            }
            bool update = latest is not null && (installed is null || ReleaseInfo.ParseVersion(latest) > ReleaseInfo.ParseVersion(installed) || tools?.Qaac is null);
            list.Add(new ComponentStatus("qaac", "qaac (Apple AAC)",
                L.T("Apple's AAC encoder, the best AAC/HE-AAC there is; used first, with fdkaac (libfdk_aac) as fallback. Apple's files are extracted from the official iTunes installer on this computer."),
                installed, tools?.Qaac, exe, latest, update && OperatingSystem.IsWindows(), OperatingSystem.IsWindows(), note));
        }

        // VapourSynth portable (Windows; Linux/macOS: the system package).
        {
            var vs = Path.Combine(AppPaths.Tools(dataDirectory), "vapoursynth", "vspipe.exe");
            bool have = File.Exists(vs);
            string? installed = have && state.TryGetValue("vapoursynth", out var vv) ? vv.Version : null;
            string? latest = null, note = OperatingSystem.IsWindows() ? null
                : L.T("Install VapourSynth with your package manager (or Homebrew): KnA finds vspipe on the PATH.");
            if (online && OperatingSystem.IsWindows() && Arch == "x64")
            {
                try { latest = (await GitHubReleases.LatestAsync("vapoursynth/vapoursynth", ct))?.Tag; }
                catch (Exception ex) when (ex is not OperationCanceledException) { note = ex.Message; }
            }
            bool update = latest is not null && (installed is null || ReleaseInfo.ParseVersion(latest.TrimStart('R', 'r')) > ReleaseInfo.ParseVersion((installed ?? "0").TrimStart('R', 'r')));
            list.Add(new ComponentStatus("vapoursynth", "VapourSynth (portable)",
                L.T("Frameserver with embedded Python, plus the BestSource and L-SMASH-Works source plugins, inside KnA's folder."),
                installed, have ? vs : tools?.VsPipe, have, latest, update, OperatingSystem.IsWindows() && Arch == "x64", note));
        }
        return list;
    }

    // ---------------------------------------------------------------- rigaya encoders

    private async Task InstallRigayaAsync(string id, string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var tool = RigayaTools.First(t => t.Id == id);
        if (!RigayaSupported) throw new PlatformNotSupportedException(L.T("Not available for this platform: use the FFmpeg encoders."));
        var rel = await GitHubReleases.LatestAsync(tool.Repo, ct) ?? throw new InvalidOperationException(L.T("No release found for {0}.", tool.Name));
        var name = PickRigayaAsset(rel.Assets.Select(a => a.Name), tool)
                   ?? throw new InvalidOperationException(L.T("No download for this platform in {0} {1}.", tool.Name, rel.Tag));
        var asset = rel.Assets.First(a => a.Name == name);
        var archive = Path.Combine(temp, name);
        log?.Report(L.T("Downloading {0}…", name));
        await GitHubReleases.DownloadAsync(asset.Url, archive, progress, ct);
        var extracted = Path.Combine(temp, "x");
        log?.Report(L.T("Extracting…"));
        var target = Path.Combine(AppPaths.Tools(dataDirectory), id);
        if (OperatingSystem.IsWindows())
        {
            await Extract7zAsync(archive, extracted, temp, progress, log, ct);
            var exe = Directory.EnumerateFiles(extracted, tool.Prefix + "64.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidDataException(L.T("The archive did not contain {0}.", tool.Prefix + "64.exe"));
            ReplaceFolder(Path.GetDirectoryName(exe)!, target);
        }
        else
        {
            await ExtractDebAsync(archive, extracted, ct);
            var bin = Directory.EnumerateFiles(extracted, id, SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("/bin/", StringComparison.Ordinal))
                      ?? throw new InvalidDataException(L.T("The archive did not contain {0}.", id));
            // tools/nvencc/nvencc + tools/nvencc/usr/lib/… (found through LD_LIBRARY_PATH by ProcessRunner).
            var staging = Path.Combine(temp, "layout");
            Directory.CreateDirectory(staging);
            File.Copy(bin, Path.Combine(staging, id));
            if (Directory.Exists(Path.Combine(extracted, "usr", "lib"))) CopyTree(Path.Combine(extracted, "usr", "lib"), Path.Combine(staging, "usr", "lib"));
            ReplaceFolder(staging, target);
            File.SetUnixFileMode(Path.Combine(target, id), (UnixFileMode)0b111_101_101);
        }
        var installed = OperatingSystem.IsWindows() ? Path.Combine(target, tool.Prefix + "64.exe") : Path.Combine(target, id);
        var r = await ProcessRunner.RunAsync(installed, ["--version"], timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (r.ExitCode != 0 && (r.StdOut + r.StdErr).Trim().Length == 0)
            throw new InvalidOperationException(L.T("The installed program does not start: {0}", r.StdErr.Trim()));
        log?.Report((r.StdOut + r.StdErr).Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? tool.Name);
        SaveState(id, rel.Tag);
    }

    /// <summary>Unpacks a .7z: 7-Zip's standalone 7zr.exe on Windows (downloaded once into tools\7zip), 7z/7za/7zr elsewhere.</summary>
    private async Task Extract7zAsync(string archive, string destination, string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        string? seven = null;
        if (OperatingSystem.IsWindows())
        {
            seven = Path.Combine(AppPaths.Tools(dataDirectory), "7zip", "7zr.exe");
            if (!File.Exists(seven))
            {
                log?.Report(L.T("Downloading {0}…", "7zr.exe (7-Zip)"));
                Directory.CreateDirectory(Path.GetDirectoryName(seven)!);
                await GitHubReleases.DownloadAsync("https://www.7-zip.org/a/7zr.exe", seven, null, ct);
            }
        }
        else seven = FfmpegLocator.LocateTool(["7z", "7za", "7zr"], dataDirectory)
                     ?? throw new InvalidOperationException(L.T("7-Zip is needed to unpack {0}: install p7zip / 7zip.", Path.GetFileName(archive)));
        var r = await ProcessRunner.RunAsync(seven, ["x", "-y", "-o" + destination, archive], timeout: TimeSpan.FromMinutes(10), ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("7-Zip: " + (r.StdErr + r.StdOut).Trim().Split('\n').LastOrDefault());
    }

    /// <summary>
    /// Unpacks a Debian package without installing it: dpkg-deb when available, otherwise the "ar" container is read
    /// here and its data.tar.* handed to tar.
    /// </summary>
    public static async Task ExtractDebAsync(string deb, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        if (FfmpegLocator.LocateTool(["dpkg-deb"], Path.GetTempPath()) is { } dpkg)
        {
            var d = await ProcessRunner.RunAsync(dpkg, ["-x", deb, destination], timeout: TimeSpan.FromMinutes(10), ct: ct);
            if (d.ExitCode == 0) return;
        }
        var data = Path.Combine(Path.GetDirectoryName(destination)!, "deb-data");
        Directory.CreateDirectory(data);
        string? tarball = null;
        await using (var fs = File.OpenRead(deb))
        {
            var magic = new byte[8];
            await fs.ReadExactlyAsync(magic, ct);
            if (System.Text.Encoding.ASCII.GetString(magic) != "!<arch>\n") throw new InvalidDataException("Not a .deb package.");
            var header = new byte[60];
            while (await fs.ReadAsync(header.AsMemory(0, 60), ct) == 60)
            {
                var name = System.Text.Encoding.ASCII.GetString(header, 0, 16).Trim().TrimEnd('/');
                long size = long.Parse(System.Text.Encoding.ASCII.GetString(header, 48, 10).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                if (name.StartsWith("data.tar", StringComparison.Ordinal))
                {
                    tarball = Path.Combine(data, name);
                    await using var outFs = File.Create(tarball);
                    var buffer = new byte[1 << 16];
                    long left = size;
                    while (left > 0)
                    {
                        int n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                        if (n <= 0) break;
                        await outFs.WriteAsync(buffer.AsMemory(0, n), ct);
                        left -= n;
                    }
                    break;
                }
                fs.Seek(size + (size % 2), SeekOrigin.Current);
            }
        }
        if (tarball is null) throw new InvalidDataException("The .deb package has no data.tar.");
        var r = await ProcessRunner.RunAsync("tar", ["-xf", tarball, "-C", destination], timeout: TimeSpan.FromMinutes(10), ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("tar: " + r.StdErr.Trim() + (tarball.EndsWith(".zst", StringComparison.Ordinal) ? " (install zstd)" : ""));
    }

    // ---------------------------------------------------------------- qaac + Apple CoreAudioToolbox

    /// <summary>The DLLs qaac needs from Apple Application Support / iTunes (the makeportable2 list).</summary>
    private static readonly string[] AppleDllPatterns =
        ["ASL.dll", "CoreAudioToolbox.dll", "CoreFoundation.dll", "icudt*.dll", "libdispatch.dll", "libicuin.dll", "libicuuc.dll", "objc.dll", "BlocksRuntime.dll"];

    private async Task InstallQaacAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(L.T("Windows only: on macOS KnA uses the same Apple encoder (AudioToolbox), elsewhere fdkaac."));
        var rel = await GitHubReleases.LatestAsync("nu774/qaac", ct) ?? throw new InvalidOperationException(L.T("No release found for {0}.", "qaac"));
        var asset = rel.Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, @"^qaac_[\d.]+\.zip$", RegexOptions.IgnoreCase))
                    ?? throw new InvalidOperationException(L.T("No download for this platform in {0} {1}.", "qaac", rel.Tag));
        var archive = Path.Combine(temp, asset.Name);
        log?.Report(L.T("Downloading {0}…", asset.Name));
        await GitHubReleases.DownloadAsync(asset.Url, archive, progress, ct);
        var extracted = Path.Combine(temp, "q");
        await ExtractAsync(archive, extracted, ct);
        var exe = Directory.EnumerateFiles(extracted, "qaac64.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException(L.T("The archive did not contain {0}.", "qaac64.exe"));
        var target = Path.Combine(AppPaths.Tools(dataDirectory), "qaac");
        var keep = Path.Combine(target, "QTfiles64");
        var keptApple = Directory.Exists(keep) ? Path.Combine(temp, "QTfiles64-keep") : null;
        if (keptApple is not null) CopyTree(keep, keptApple);
        ReplaceFolder(Path.GetDirectoryName(exe)!, target);
        if (keptApple is not null) CopyTree(keptApple, keep);

        if (ForPackaging)
        {
            // Release packages carry qaac only: Apple's files are extracted on the user's machine (Install in Components).
            SaveState("qaac", rel.Tag.TrimStart('v'));
            return;
        }
        if (!File.Exists(Path.Combine(keep, "CoreAudioToolbox.dll")) && !AppleInstalled())
            await ExtractCoreAudioAsync(temp, keep, progress, log, ct);

        var check = await ProcessRunner.RunAsync(Path.Combine(target, "qaac64.exe"), ["--check"], timeout: TimeSpan.FromSeconds(30), ct: ct);
        var text = (check.StdOut + check.StdErr).Trim();
        log?.Report(text.Split('\n').FirstOrDefault(l => l.Contains("CoreAudioToolbox", StringComparison.OrdinalIgnoreCase))?.Trim() ?? text.Split('\n').FirstOrDefault()?.Trim() ?? "qaac");
        if (!text.Contains("CoreAudioToolbox", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("qaac cannot load Apple's CoreAudioToolbox: {0}", text.Split('\n').LastOrDefault()?.Trim() ?? ""));
        SaveState("qaac", rel.Tag.TrimStart('v'));
    }

    private static bool AppleInstalled() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Apple", "Apple Application Support", "CoreAudioToolbox.dll"))
        || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "iTunes", "CoreAudioToolbox.dll"));

    /// <summary>
    /// makeportable2, done here: the official iTunes x64 installer is downloaded from Apple, its MSI packages are
    /// unpacked (iTunes64Setup.exe /extract, then an administrative "msiexec /a" that installs nothing), and the
    /// CoreAudioToolbox DLLs are copied next to qaac64.exe in QTfiles64. Nothing of Apple is redistributed by KnA.
    /// </summary>
    private static async Task ExtractCoreAudioAsync(string temp, string target, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "kna-coreaudio-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var setup = Path.Combine(root, "iTunes64Setup.exe");
            log?.Report(L.T("Downloading {0}…", "iTunes64Setup.exe (Apple)"));
            await GitHubReleases.DownloadAsync("https://www.apple.com/itunes/download/win64", setup, progress, ct);
            var msis = Path.Combine(root, "msi");
            Directory.CreateDirectory(msis);
            log?.Report(L.T("Extracting…"));
            var x = await ProcessRunner.RunAsync(setup, ["/extract", msis], timeout: TimeSpan.FromMinutes(5), ct: ct);
            var packages = Directory.EnumerateFiles(msis, "*.msi", SearchOption.AllDirectories)
                .OrderByDescending(f => Path.GetFileName(f).Contains("AppleApplicationSupport64", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => Path.GetFileName(f).Contains("iTunes64", StringComparison.OrdinalIgnoreCase)).ToList();
            if (packages.Count == 0) throw new InvalidOperationException(L.T("The iTunes installer could not be unpacked ({0}).", x.ExitCode));
            int i = 0;
            foreach (var msi in packages)
            {
                var dir = Path.Combine(root, "a" + i++);
                await ProcessRunner.RunAsync("msiexec", ["/a", msi, "/qn", "TARGETDIR=" + dir], timeout: TimeSpan.FromMinutes(10), ct: ct);
                var core = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "CoreAudioToolbox.dll", SearchOption.AllDirectories).FirstOrDefault() : null;
                if (core is null) continue;
                var from = Path.GetDirectoryName(core)!;
                Directory.CreateDirectory(target);
                foreach (var pattern in AppleDllPatterns)
                    foreach (var f in Directory.EnumerateFiles(from, pattern))
                        File.Copy(f, Path.Combine(target, Path.GetFileName(f)), overwrite: true);
                log?.Report(L.T("Apple's CoreAudioToolbox extracted to {0}", target));
                return;
            }
            throw new InvalidOperationException(L.T("CoreAudioToolbox.dll was not found in the iTunes installer."));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- VapourSynth portable (Windows)

    private async Task InstallVapourSynthAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || Arch != "x64")
            throw new PlatformNotSupportedException(L.T("Install VapourSynth with your package manager (or Homebrew): KnA finds vspipe on the PATH."));
        var rel = await GitHubReleases.LatestAsync("vapoursynth/vapoursynth", ct) ?? throw new InvalidOperationException(L.T("No release found for {0}.", "VapourSynth"));
        var asset = rel.Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, @"^VapourSynth64-Portable-R\d+\.zip$", RegexOptions.IgnoreCase))
                    ?? throw new InvalidOperationException(L.T("No download for this platform in {0} {1}.", "VapourSynth", rel.Tag));
        var archive = Path.Combine(temp, asset.Name);
        log?.Report(L.T("Downloading {0}…", asset.Name));
        await GitHubReleases.DownloadAsync(asset.Url, archive, progress, ct);
        var extracted = Path.Combine(temp, "vs");
        await ExtractAsync(archive, extracted, ct);
        var target = Path.Combine(AppPaths.Tools(dataDirectory), "vapoursynth");
        string plugins, vspipeExe;
        if (Directory.EnumerateFiles(extracted, "vapoursynth-*.whl", SearchOption.AllDirectories).FirstOrDefault() is { } wheel)
        {
            // R74 and later: the portable zip holds the pip wheel and two .bat files. VapourSynth then needs no
            // configuration when python.exe and python3.dll sit in the folder above Lib\site-packages: a portable Python
            // (3.12+) goes there, the wheel is installed into it, and vspipe.exe lives in the package folder.
            log?.Report(L.T("Downloading a portable Python (no installation, stays in KnA's data folder)…"));
            var py = await DownloadPortablePythonAsync(temp, null, progress, log, ct);
            ReplaceFolder(py, target);
            foreach (var bat in Directory.EnumerateFiles(extracted, "*.bat", SearchOption.AllDirectories))
                File.Copy(bat, Path.Combine(target, Path.GetFileName(bat)), overwrite: true);
            log?.Report(L.T("Installing {0}…", Path.GetFileName(wheel)));
            var pip = await ProcessRunner.RunAsync(Path.Combine(target, "python.exe"),
                ["-m", "pip", "install", "--no-warn-script-location", "--disable-pip-version-check", wheel],
                workingDirectory: target, timeout: TimeSpan.FromMinutes(10), ct: ct);
            if (pip.ExitCode != 0)
                throw new InvalidOperationException("pip: " + (pip.StdErr.Trim().Split('\n').LastOrDefault() ?? pip.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var package = Path.Combine(target, "Lib", "site-packages", "vapoursynth");
            vspipeExe = Path.Combine(package, "vspipe.exe");
            if (!File.Exists(vspipeExe)) throw new InvalidDataException(L.T("The archive did not contain {0}.", "vspipe.exe"));
            // Autoloaded plugin folder of the pip package.
            plugins = Path.Combine(package, "plugins");
        }
        else
        {
            var vspipe = Directory.EnumerateFiles(extracted, "vspipe.exe", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidDataException(L.T("The archive did not contain {0}.", "vspipe.exe"));
            ReplaceFolder(Path.GetDirectoryName(vspipe)!, target);
            // Older portable zips (up to R73): vspipe loads python3NN.dll from its folder; when the zip doesn't ship it,
            // the version VapourSynth was built for is read from its DLLs and that Python (portable build) is added.
            if (Directory.EnumerateFiles(target, "python3*.dll").All(f => Path.GetFileName(f).Equals("python3.dll", StringComparison.OrdinalIgnoreCase))
                && RequiredPythonMinor(target) is { } minor)
            {
                log?.Report(L.T("Downloading a portable Python (no installation, stays in KnA's data folder)…"));
                var py = await DownloadPortablePythonAsync(temp, minor, progress, log, ct);
                foreach (var dir in Directory.EnumerateDirectories(py, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(py, dir)));
                foreach (var file in Directory.EnumerateFiles(py, "*", SearchOption.AllDirectories))
                {
                    var dst = Path.Combine(target, Path.GetRelativePath(py, file));
                    if (!File.Exists(dst)) File.Copy(file, dst);
                }
            }
            vspipeExe = Path.Combine(target, "vspipe.exe");
            plugins = Path.Combine(target, "vs-plugins");
        }
        Directory.CreateDirectory(plugins);

        // Source plugins: BestSource (vapoursynth/bestsource) and L-SMASH-Works.
        foreach (var (repo, pattern, dll) in new[]
        {
            ("vapoursynth/bestsource", @"^BestSource-R\d+-win64-msvc\.zip$", "BestSource.dll"),
            ("HomeOfAviSynthPlusEvolution/L-SMASH-Works", @"^L-SMASH-Works-r[\d.]+\.7z$", "LSMASHSource.dll"),
        })
        {
            try
            {
                var prel = await GitHubReleases.LatestAsync(repo, ct);
                var pa = prel?.Assets.FirstOrDefault(a => Regex.IsMatch(a.Name, pattern, RegexOptions.IgnoreCase));
                if (pa is null) { log?.Report(L.T("No download for this platform in {0} {1}.", repo, prel?.Tag ?? "?")); continue; }
                var file = Path.Combine(temp, pa.Name);
                log?.Report(L.T("Downloading {0}…", pa.Name));
                await GitHubReleases.DownloadAsync(pa.Url, file, null, ct);
                var px = Path.Combine(temp, "p-" + Path.GetFileNameWithoutExtension(pa.Name));
                if (file.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) await Extract7zAsync(file, px, temp, null, log, ct);
                else await ExtractAsync(file, px, ct);
                // 64-bit plugin only (some archives ship x86 and x64 builds side by side).
                var found = Directory.EnumerateFiles(px, dll, SearchOption.AllDirectories)
                    .OrderByDescending(f => f.Contains("64", StringComparison.Ordinal)).FirstOrDefault();
                if (found is not null) File.Copy(found, Path.Combine(plugins, dll), overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or IOException) { log?.Report(repo + ": " + ex.Message); }
        }
        var r = await ProcessRunner.RunAsync(vspipeExe, ["--version"], workingDirectory: target, timeout: TimeSpan.FromSeconds(30), ct: ct);
        log?.Report((r.StdOut + r.StdErr).Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "VapourSynth");
        SaveState("vapoursynth", rel.Tag);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>"python313.dll" named inside VapourSynth's DLLs → 13.</summary>
    internal static int? RequiredPythonMinor(string folder)
    {
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll").Concat(Directory.EnumerateFiles(folder, "*.pyd", SearchOption.AllDirectories)))
        {
            try
            {
                var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(dll));
                var m = Regex.Match(text, @"python3(\d{2})\.dll", RegexOptions.IgnoreCase);
                if (m.Success) return int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException) { }
        }
        return null;
    }

    private static void ReplaceFolder(string source, string target)
    {
        if (Directory.Exists(target))
        {
            var old = target + ".old";
            try { if (Directory.Exists(old)) Directory.Delete(old, true); Directory.Move(target, old); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        CopyTree(source, target);
        try { if (Directory.Exists(target + ".old")) Directory.Delete(target + ".old", true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
