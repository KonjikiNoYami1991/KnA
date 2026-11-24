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

        // VapourSynth: KnA's portable copy (Python + VapourSynth + the catalogue's plugins), every platform with a portable Python.
        {
            var vs = FfmpegLocator.LocateVsPipe(dataDirectory);
            bool own = vs is not null && VapourSynthRuntime.RootOf(vs) is { } root
                       && (root.StartsWith(AppPaths.Tools(dataDirectory), StringComparison.OrdinalIgnoreCase) || root.StartsWith(AppPaths.Bundled("tools"), StringComparison.OrdinalIgnoreCase));
            string? installed = own && state.TryGetValue("vapoursynth", out var vv) ? vv.Version : own ? "?" : null;
            bool supported = PortablePythonTriple() is not null;
            string? note = supported ? null : L.T("Install VapourSynth with your package manager (or Homebrew): KnA finds vspipe on the PATH.");
            bool update = supported && installed != VapourSynthRuntime.Release;
            list.Add(new ComponentStatus("vapoursynth", "VapourSynth (portable)",
                L.T("Frameserver with its own Python and every plugin of the filter catalogue (QTGMC, SMDegrain, DFTTest, KNLMeansCL, f3kdb, BestSource, L-SMASH…), inside KnA's folder."),
                installed, vs ?? tools?.VsPipe, own, VapourSynthRuntime.Release, update, supported, note));
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

    // ---------------------------------------------------------------- VapourSynth portable (every platform)

    /// <summary>
    /// A portable Python 3.13 in tools/vapoursynth with the official VapourSynth wheel, vsjetpack and the catalogue's
    /// plugins (all wheels, fixed versions: see <see cref="VapourSynthRuntime"/>), then a check that the plugins load.
    /// </summary>
    private async Task InstallVapourSynthAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (PortablePythonTriple() is null)
            throw new PlatformNotSupportedException(L.T("Install VapourSynth with your package manager (or Homebrew): KnA finds vspipe on the PATH."));
        log?.Report(L.T("Downloading a portable Python (no installation, stays in KnA's data folder)…"));
        var py = await DownloadPortablePythonAsync(temp, VapourSynthRuntime.PythonMinor, progress, log, ct, stripped: true);
        var target = Path.Combine(AppPaths.Tools(dataDirectory), "vapoursynth");
        ReplaceFolder(py, target);
        var python = VapourSynthRuntime.PythonOf(target)!;
        if (!OperatingSystem.IsWindows() && File.Exists(python))
            File.SetUnixFileMode(python, File.GetUnixFileMode(python) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        async Task<ProcessResult> Pip(IEnumerable<string> packages) => await ProcessRunner.RunAsync(python,
            ["-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location", "--no-input", "--only-binary=:all:", .. packages],
            workingDirectory: target, timeout: TimeSpan.FromMinutes(30), ct: ct);
        log?.Report(L.T("Installing {0}…", "VapourSynth " + VapourSynthRuntime.Release + ", vsjetpack, plugins"));
        var r = await Pip(VapourSynthRuntime.Required(Arch));
        if (r.ExitCode != 0)
            throw new InvalidOperationException("pip: " + ((r.StdErr + r.StdOut).Trim().Split('\n').LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal)) ?? r.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var opt in VapourSynthRuntime.Optional)
        {
            var o = await Pip([opt]);
            if (o.ExitCode != 0) log?.Report(L.T("{0}: no build for this platform, skipped.", opt.Split("==")[0]));
        }

        PruneVapourSynth(target);

        var vspipe = FfmpegLocator.LocateVsPipe(dataDirectory) is { } found && VapourSynthRuntime.RootOf(found) == Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar)
            ? found : null;
        if (vspipe is null) throw new InvalidDataException(L.T("The archive did not contain {0}.", FfmpegLocator.ExeName("vspipe")));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(vspipe, File.GetUnixFileMode(vspipe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        if (!VapourSynthRuntime.Prepare(vspipe, dataDirectory))
            throw new InvalidDataException(L.T("VapourSynth could not be configured: {0}", target));

        // The catalogue's plugins and modules must load (vspipe runs a tiny script).
        var check = Path.Combine(temp, "kna-check.vpy");
        await File.WriteAllTextAsync(check, VapourSynthRuntime.SmokeTest, ct);
        var t = await ProcessRunner.RunAsync(vspipe, ["--info", check], workingDirectory: temp, timeout: TimeSpan.FromMinutes(5), ct: ct);
        if (t.ExitCode != 0)
            throw new InvalidOperationException(L.T("The VapourSynth script failed: {0}", (t.StdErr + t.StdOut).Trim().Split('\n').LastOrDefault() ?? ""));
        var v = await ProcessRunner.RunAsync(vspipe, ["--version"], workingDirectory: target, timeout: TimeSpan.FromSeconds(30), ct: ct);
        log?.Report((v.StdOut + v.StdErr).Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "VapourSynth");
        SaveState("vapoursynth", VapourSynthRuntime.Release);
    }

    /// <summary>What a frameserver never uses: Python's own test suite, Tk/IDLE, headers, and the packages' test folders.</summary>
    private static void PruneVapourSynth(string root)
    {
        var dirs = new List<string>();
        foreach (var lib in new[] { Path.Combine(root, "Lib") }.Concat(Directory.Exists(Path.Combine(root, "lib"))
                     ? Directory.EnumerateDirectories(Path.Combine(root, "lib"), "python3.*") : []))
            dirs.AddRange(new[] { "test", "idlelib", "tkinter", "turtledemo" }.Select(d => Path.Combine(lib, d)));
        dirs.AddRange(new[] { "include", "share", "tcl" }.Select(d => Path.Combine(root, d)));
        if (Directory.Exists(Path.Combine(root, "lib")))
            dirs.AddRange(Directory.EnumerateDirectories(Path.Combine(root, "lib")).Where(d => Path.GetFileName(d) is var n && (n.StartsWith("tcl", StringComparison.Ordinal) || n.StartsWith("tk", StringComparison.Ordinal))));
        dirs.AddRange(Directory.EnumerateDirectories(root, "tests", SearchOption.AllDirectories).Where(d => d.Contains("site-packages", StringComparison.Ordinal)));
        foreach (var d in dirs.Where(Directory.Exists))
            try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- helpers

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
        {
            var dst = Path.Combine(target, Path.GetRelativePath(source, file));
            // Symbolic links stay links (a portable Python has python → python3.13, libpython….so → ….so.1.0: copies would
            // double its size).
            if (!OperatingSystem.IsWindows() && new FileInfo(file).LinkTarget is { } link)
            {
                if (File.Exists(dst) || new FileInfo(dst).LinkTarget is not null) File.Delete(dst);
                File.CreateSymbolicLink(dst, link);
            }
            else File.Copy(file, dst, overwrite: true);
        }
    }
}
