using System.Diagnostics;
using System.Runtime.InteropServices;
using KnA.Core.Localization;
using KnA.Core.Queue;

namespace KnA.Core.Tools;

public sealed record UpdateCheck(Version Current, ReleaseInfo? Latest, bool IsNewer, ReleaseAsset? Asset, string Message);

/// <summary>
/// Checks GitHub for a newer KnA release and installs it in place. One complete package per system
/// (.github/workflows/release.yml): KnA-Windows-x64.zip, KnA-Windows-ARM64.zip, KnA-macOS-AppleSilicon.zip,
/// KnA-macOS-Intel.zip, KnA-Linux-x64.tar.gz, KnA-Linux-ARM64.tar.gz and KnA-Server-{Windows-x64, Linux-x64,
/// Linux-ARM64}; the update takes only the program files from it (tools, models and data stay as they are).
/// </summary>
public static class AppUpdater
{
    public const string Repository = "KonjikiNoYami1991/KnA";

    public static string RuntimeId =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    /// <summary>Package of a flavor ("desktop" / "server") for a runtime id, as the release workflow names it.</summary>
    public static string AssetName(string flavor, string rid)
    {
        var (os, arch) = rid switch
        {
            "win-x64" => ("Windows", "x64"), "win-arm64" => ("Windows", "ARM64"),
            "osx-arm64" => ("macOS", "AppleSilicon"), "osx-x64" => ("macOS", "Intel"),
            "linux-arm64" => ("Linux", "ARM64"), _ => ("Linux", "x64"),
        };
        string ext = os == "Linux" ? "tar.gz" : "zip";
        return flavor == "server" ? $"KnA-Server-{os}-{arch}.{ext}" : $"KnA-{os}-{arch}.{ext}";
    }

    /// <summary>Folders of a package that belong to the user's installation, never replaced by an update.</summary>
    private static readonly string[] KeepFolders = ["tools", "ai", "data"];

    public static Version CurrentVersion => ReleaseInfo.ParseVersion(AppInfo.Version) ?? new Version(0, 0);

    public static async Task<UpdateCheck> CheckAsync(string flavor, CancellationToken ct = default)
    {
        var latest = await GitHubReleases.LatestAsync(Repository, ct);
        if (latest is null) return new UpdateCheck(CurrentVersion, null, false, null, L.T("No release has been published on GitHub yet."));
        var v = latest.Version;
        bool newer = v is not null && v > CurrentVersion;
        var asset = latest.Assets.FirstOrDefault(a => a.Name.Equals(AssetName(flavor, RuntimeId), StringComparison.OrdinalIgnoreCase))
                    // releases up to 3.2.1
                    ?? latest.Assets.FirstOrDefault(a => a.Name.Equals($"KnA-{flavor}-{RuntimeId}.zip", StringComparison.OrdinalIgnoreCase));
        return new UpdateCheck(CurrentVersion, latest, newer, asset,
            newer ? L.T("Version {0} is available (you have {1}).", latest.Tag.TrimStart('v'), AppInfo.Version)
                  : L.T("You have the latest version ({0}).", AppInfo.Version));
    }

    /// <summary>
    /// Downloads the release zip and swaps the program files. Running files are renamed to *.old first
    /// (allowed on Windows even for the running .exe), so the new version starts at the next launch.
    /// </summary>
    public static async Task InstallAsync(ReleaseAsset asset, string dataDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        var temp = Path.Combine(dataDirectory, "temp", "update-" + Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(temp, asset.Name);
        await GitHubReleases.DownloadAsync(asset.Url, zip, progress, ct);
        var staging = Path.Combine(temp, "files");
        await ComponentManager.ExtractAsync(zip, staging, ct);

        var appDir = AppContext.BaseDirectory;
        // Packages contain a single top folder (KnA/, KnA-server/, KnA.app/): the program files are inside it, in
        // Contents/MacOS for the macOS app.
        var root = staging;
        while (Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is [var only]) root = only;
        if (Directory.Exists(Path.Combine(root, "Contents", "MacOS"))) root = Path.Combine(root, "Contents", "MacOS");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (KeepFolders.Contains(first, StringComparer.OrdinalIgnoreCase) || rel.Equals(KnA.Core.Settings.AppEnv.PortableMarker, StringComparison.OrdinalIgnoreCase))
                continue;
            var dst = Path.Combine(appDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            ComponentManager.ReplaceFile(file, dst);
            if (!OperatingSystem.IsWindows() && Path.GetFileNameWithoutExtension(dst) is "KnA" or "kna-server" or "KnA-server")
                File.SetUnixFileMode(dst, File.GetUnixFileMode(dst) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        // macOS: the app's Info.plist carries the version shown by Finder.
        var plist = Path.Combine(root, "..", "Info.plist");
        if (OperatingSystem.IsMacOS() && File.Exists(plist) && Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(appDir))) == "Contents")
            ComponentManager.ReplaceFile(Path.GetFullPath(plist), Path.Combine(appDir, "..", "Info.plist"));
        // From 3.2 the program is a single executable: the loose .NET/Avalonia files of an older version are
        // set aside (deleted at the next start), nothing else in the folder is touched.
        var shipped = Directory.EnumerateFiles(root).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!shipped.Contains("KnA.Core.dll"))
            foreach (var f in Directory.EnumerateFiles(appDir))
                if (Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".pdb" or ".json" or ".so" or ".dylib"
                    && !shipped.Contains(Path.GetFileName(f)) && !Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
                    try { File.Move(f, f + ".old", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { Directory.Delete(temp, true); } catch (IOException) { }
    }

    /// <summary>Removes "*.old" leftovers of a previous update (call at startup).</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old", SearchOption.AllDirectories))
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
    }

    public static void Restart(IEnumerable<string>? args = null)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args ?? []) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
