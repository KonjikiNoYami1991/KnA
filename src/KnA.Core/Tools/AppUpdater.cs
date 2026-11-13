using System.Diagnostics;
using System.Runtime.InteropServices;
using KnA.Core.Localization;
using KnA.Core.Queue;

namespace KnA.Core.Tools;

public sealed record UpdateCheck(Version Current, ReleaseInfo? Latest, bool IsNewer, ReleaseAsset? Asset, string Message);

/// <summary>
/// Checks GitHub for a newer KnA release and (desktop builds) installs it in place.
/// Release assets are expected to be named "KnA-desktop-{rid}.zip" / "KnA-server-{rid}.zip"
/// (produced by .github/workflows/release.yml when a "v*" tag is pushed).
/// </summary>
public static class AppUpdater
{
    public const string Repository = "KonjikiNoYami1991/TnA";

    public static string RuntimeId =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    public static Version CurrentVersion => ReleaseInfo.ParseVersion(AppInfo.Version) ?? new Version(0, 0);

    public static async Task<UpdateCheck> CheckAsync(string flavor, CancellationToken ct = default)
    {
        var latest = await GitHubReleases.LatestAsync(Repository, ct);
        if (latest is null) return new UpdateCheck(CurrentVersion, null, false, null, L.T("No release has been published on GitHub yet."));
        var v = latest.Version;
        bool newer = v is not null && v > CurrentVersion;
        var asset = latest.Assets.FirstOrDefault(a => a.Name.Equals($"KnA-{flavor}-{RuntimeId}.zip", StringComparison.OrdinalIgnoreCase));
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
        // Zips may contain a single top folder.
        var root = Directory.GetFiles(staging).Length == 0 && Directory.GetDirectories(staging) is [var only] ? only : staging;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            var dst = Path.Combine(appDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            ComponentManager.ReplaceFile(file, dst);
        }
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
