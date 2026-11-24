using System.Text.RegularExpressions;
using KnA.Core.Localization;
using KnA.Core.Processing;
using KnA.Core.Settings;

namespace KnA.Core.Tools;

/// <summary>
/// MKVToolNix (mkvmerge, GPLv2, https://mkvtoolnix.download): KnA uses it for one job only, writing the Dolby Vision
/// configuration record of a re-encoded Dolby Vision video (FFmpeg writes only the RPU in the video stream).
///  • Windows (x64; Windows on Arm runs it through x64 emulation): the official portable 7z;
///  • Linux x64: the official AppImage, unpacked (no FUSE needed) with a small launcher next to its libraries;
///  • macOS and Linux ARM64: the release packages carry it (Homebrew / the distribution's package with its
///    libraries); otherwise Homebrew or the package manager, which KnA finds by itself.
/// </summary>
public sealed partial class ComponentManager
{
    private const string MkvToolNixSite = "https://mkvtoolnix.download";

    /// <summary>This platform can download MKVToolNix from its site.</summary>
    public static bool MkvtoolnixDownloadable() => OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && Arch == "x64");

    [GeneratedRegex(@"<version>\s*(\d+(?:\.\d+)+)\s*</version>", RegexOptions.CultureInvariant)]
    private static partial Regex XmlVersion();
    [GeneratedRegex(@"href=""(\d+(?:\.\d+)+)/""", RegexOptions.CultureInvariant)]
    private static partial Regex FolderVersion();

    /// <summary>Latest release: the site's latest-release.xml, or the newest folder of the Windows releases.</summary>
    public static async Task<string?> LatestMkvtoolnixAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KnA");
        try
        {
            var xml = await http.GetStringAsync(MkvToolNixSite + "/latest-release.xml", ct);
            if (XmlVersion().Match(xml) is { Success: true } m) return m.Groups[1].Value;
        }
        catch (HttpRequestException) { }
        var list = await http.GetStringAsync(MkvToolNixSite + "/windows/releases/", ct);
        return FolderVersion().Matches(list).Select(x => x.Groups[1].Value).OrderBy(ReleaseInfo.ParseVersion).LastOrDefault();
    }

    private async Task InstallMkvtoolnixAsync(string temp, IProgress<double>? progress, IProgress<string>? log, CancellationToken ct)
    {
        if (!MkvtoolnixDownloadable())
            throw new PlatformNotSupportedException(L.T("MKVToolNix comes with KnA's package here; otherwise install it with Homebrew or the package manager (mkvtoolnix): KnA finds mkvmerge by itself."));
        var version = await LatestMkvtoolnixAsync(ct) ?? throw new InvalidOperationException(L.T("No release found for {0}.", "MKVToolNix"));
        var target = FfmpegLocator.ManagedDir(dataDirectory, "mkvtoolnix");
        var extracted = Path.Combine(temp, "mkv");
        if (OperatingSystem.IsWindows())
        {
            var name = $"mkvtoolnix-64-bit-{version}.7z";
            var archive = Path.Combine(temp, name);
            log?.Report(L.T("Downloading {0}…", name));
            await GitHubReleases.DownloadAsync($"{MkvToolNixSite}/windows/releases/{version}/{name}", archive, progress, ct);
            if (!ArchiveEngine.IsInstalled(dataDirectory)) await ArchiveEngine.InstallAsync(this, dataDirectory, temp, null, log, ct);
            log?.Report(L.T("Extracting…"));
            await ArchiveEngine.ExtractAsync(dataDirectory, archive, extracted, ct);
            var exe = Directory.EnumerateFiles(extracted, "mkvmerge.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidDataException(L.T("The archive did not contain {0}.", "mkvmerge.exe"));
            ReplaceFolder(Path.GetDirectoryName(exe)!, target);
        }
        else
        {
            var name = $"MKVToolNix_GUI-{version}-x86_64.AppImage";
            var image = Path.Combine(temp, name);
            log?.Report(L.T("Downloading {0}…", name));
            await GitHubReleases.DownloadAsync($"{MkvToolNixSite}/appimage/{name}", image, progress, ct);
            File.SetUnixFileMode(image, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(extracted);
            log?.Report(L.T("Extracting…"));
            // An AppImage unpacks itself without FUSE.
            var x = await ProcessRunner.RunAsync(image, ["--appimage-extract"], extracted, TimeSpan.FromMinutes(10), ct);
            var root = Path.Combine(extracted, "squashfs-root");
            var bin = Directory.Exists(root) ? Directory.EnumerateFiles(root, "mkvmerge", SearchOption.AllDirectories).FirstOrDefault() : null;
            if (bin is null) throw new InvalidDataException(L.T("The archive did not contain {0}.", "mkvmerge") + " " + x.StdErr.Trim());
            var appDir = Path.Combine(target, "appimage");
            ReplaceFolder(root, appDir);
            var rel = Path.GetRelativePath(root, bin).Replace('\\', '/');
            var libs = string.Join(':', new[] { "usr/lib", "usr/lib64", "lib", Path.GetDirectoryName(rel)!.Replace('\\', '/') + "/../lib" }
                .Select(l => "$d/appimage/" + l));
            WriteLauncher(Path.Combine(target, "mkvmerge"), "$d/appimage/" + rel, libs);
        }
        var mkvmerge = Path.Combine(target, FfmpegLocator.ExeName("mkvmerge"));
        var r = await ProcessRunner.RunAsync(mkvmerge, ["--version"], timeout: TimeSpan.FromSeconds(30), ct: ct);
        if (!r.StdOut.Contains("mkvmerge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("The installed program does not start: {0}", (r.StdErr + r.StdOut).Trim()));
        log?.Report(r.StdOut.Trim().Split('\n')[0]);
        SaveState("mkvtoolnix", version);
    }

    /// <summary>A launcher script that starts a program with its own libraries (Linux), wherever the folder is moved.</summary>
    internal static void WriteLauncher(string path, string program, string libraryPath)
    {
        File.WriteAllText(path, "#!/bin/sh\nd=$(dirname \"$(readlink -f \"$0\")\")\n" +
            $"LD_LIBRARY_PATH=\"{libraryPath}${{LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}}\" exec \"{program}\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private async Task<ComponentStatus> CheckMkvtoolnixAsync(FfmpegTools? tools, bool online, CancellationToken ct)
    {
        var path = tools?.Mkvmerge;
        string? installed = null;
        if (path is not null && await ReadVersionAsync(path, ["--version"], ct) is { } v && Regex.Match(v, @"v(\d+(?:\.\d+)+)") is { Success: true } m)
            installed = m.Groups[1].Value;
        string? latest = null, note = MkvtoolnixDownloadable() ? null
            : L.T("MKVToolNix comes with KnA's package here; otherwise install it with Homebrew or the package manager (mkvtoolnix): KnA finds mkvmerge by itself.");
        if (online && MkvtoolnixDownloadable())
        {
            try { latest = await LatestMkvtoolnixAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { note = ex.Message; }
        }
        bool update = latest is not null && (installed is null || ReleaseInfo.ParseVersion(latest) > ReleaseInfo.ParseVersion(installed));
        return new ComponentStatus("mkvtoolnix", "MKVToolNix (mkvmerge)",
            L.T("Writes the Dolby Vision configuration record when Dolby Vision is kept (TVs and players need it to see Dolby Vision)."),
            installed, path, path?.StartsWith(AppPaths.Tools(dataDirectory), StringComparison.OrdinalIgnoreCase) ?? false,
            latest, update, MkvtoolnixDownloadable(), note);
    }
}
