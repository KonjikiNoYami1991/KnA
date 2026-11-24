using System.Security.Cryptography;

namespace KnA.Core.Tools;

/// <summary>
/// A cheap description of what the hardware test depends on: the GPUs with their driver versions and the encoder
/// programs (FFmpeg builds, NVEncC / QSVEncC / VCEEncC) with their size and date. Computed at every start in a few
/// milliseconds; when it differs from the one stored with the last test (a new graphics card, a driver update, an
/// updated FFmpeg), the test runs again by itself so the GPU encoders never stay "not detected" or wrong.
/// </summary>
public static class MachineFingerprint
{
    public static string Compute(FfmpegTools? tools, IEnumerable<string>? customBuilds = null)
    {
        var parts = new List<string>(Gpus());
        if (tools is not null)
            foreach (var exe in new[] { tools.Ffmpeg, tools.NvencC, tools.QsvencC, tools.VceencC })
                parts.Add(FileStamp(exe));
        foreach (var b in customBuilds ?? []) parts.Add(FileStamp(b));
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts)));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    /// <summary>Program file: path, size and date (an update changes them).</summary>
    internal static string FileStamp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "-";
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }

    /// <summary>The GPUs and their drivers, as the system lists them (no external program is started).</summary>
    public static IEnumerable<string> Gpus()
    {
        var list = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows()) list.AddRange(WindowsGpus());
            else if (OperatingSystem.IsLinux()) list.AddRange(LinuxGpus());
            else if (OperatingSystem.IsMacOS()) list.Add("macOS " + Environment.OSVersion.VersionString + " " + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static IEnumerable<string> LinuxGpus()
    {
        const string drm = "/sys/class/drm";
        if (Directory.Exists(drm))
            foreach (var card in Directory.GetDirectories(drm, "card*").Where(d => !Path.GetFileName(d).Contains('-')))
            {
                var dev = Path.Combine(card, "device");
                string Read(string f) { try { return File.ReadAllText(Path.Combine(dev, f)).Trim(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; } }
                var driver = "";
                try { if (Directory.Exists(Path.Combine(dev, "driver"))) driver = Path.GetFileName(new DirectoryInfo(Path.Combine(dev, "driver")).ResolveLinkTarget(true)?.FullName ?? ""); }
                catch (IOException) { }
                yield return $"{Read("vendor")}:{Read("device")}:{driver}:{ModuleVersion(driver)}";
            }
        foreach (var f in new[] { "/proc/driver/nvidia/version", "/sys/module/nvidia/version" })
            if (File.Exists(f)) yield return File.ReadLines(f).FirstOrDefault()?.Trim() ?? "";
        // VA-API / Vulkan / Level Zero user-space drivers come with the system: their packages' dates.
        foreach (var f in new[] { "/usr/lib/x86_64-linux-gnu/dri/iHD_drv_video.so", "/usr/lib/x86_64-linux-gnu/dri/radeonsi_drv_video.so",
                     "/usr/lib/x86_64-linux-gnu/libvpl.so.2", "/usr/lib/x86_64-linux-gnu/libamfrt64.so.1", "/usr/lib/x86_64-linux-gnu/libnvidia-encode.so.1" })
            if (File.Exists(f)) yield return FileStamp(f);
    }

    private static string ModuleVersion(string driver)
    {
        if (driver.Length == 0) return "";
        try { var f = $"/sys/module/{driver}/version"; return File.Exists(f) ? File.ReadAllText(f).Trim() : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<string> WindowsGpus()
    {
        // Display adapters class: one subkey per adapter with its name and driver version.
        using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) yield break;
        foreach (var name in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            using var k = cls.OpenSubKey(name);
            if (k?.GetValue("DriverDesc") is string desc)
                yield return $"{desc}|{k.GetValue("DriverVersion")}|{k.GetValue("MatchingDeviceId")}";
        }
    }
}
