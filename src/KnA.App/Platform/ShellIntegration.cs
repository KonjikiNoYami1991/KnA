using System.Diagnostics;
using System.Text;
using KnA.Core.Localization;

namespace KnA.App.Platform;

/// <summary>
/// "Send to" integration.
/// Windows: shortcuts in the Send To folder — "KnA" (add to the queue) plus one per chosen preset
/// ("KnA — preset": add with that preset and start). Several files sent at once arrive in the same instance.
/// Linux: a .desktop launcher (Open with…), Nautilus scripts and a Dolphin service menu with the same entries.
/// </summary>
public static class ShellIntegration
{
    private const string Prefix = "KnA";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static string? ExePath => Environment.ProcessPath;

    public sealed record Entry(string Name, string Arguments);

    public static IReadOnlyList<Entry> Entries(IEnumerable<string> presets)
    {
        var list = new List<Entry> { new(Prefix, "") };
        foreach (var p in presets.Distinct())
            list.Add(new($"{Prefix} — {p}", $"--preset \"{p.Replace("\"", "")}\" --start"));
        return list;
    }

    /// <summary>Is the base entry installed?</summary>
    public static bool IsInstalled()
    {
        if (OperatingSystem.IsWindows()) return File.Exists(Path.Combine(SendToFolder, Prefix + ".lnk"));
        if (OperatingSystem.IsLinux()) return File.Exists(DesktopFile);
        return false;
    }

    /// <summary>Removes the old entries and writes the current ones.</summary>
    public static async Task InstallAsync(IEnumerable<string> presets)
    {
        Uninstall();
        var exe = ExePath ?? throw new InvalidOperationException("Unknown executable path.");
        var entries = Entries(presets);
        if (OperatingSystem.IsWindows()) await InstallWindowsAsync(exe, entries);
        else if (OperatingSystem.IsLinux()) InstallLinux(exe, entries);
        else throw new PlatformNotSupportedException();
    }

    public static void Uninstall()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var f in Directory.EnumerateFiles(SendToFolder, Prefix + "*.lnk"))
                    if (Path.GetFileName(f) == Prefix + ".lnk" || Path.GetFileName(f).StartsWith(Prefix + " — ", StringComparison.Ordinal)) File.Delete(f);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (File.Exists(DesktopFile)) File.Delete(DesktopFile);
                if (File.Exists(DolphinFile)) File.Delete(DolphinFile);
                if (Directory.Exists(NautilusDir))
                    foreach (var f in Directory.EnumerateFiles(NautilusDir, Prefix + "*"))
                        if (File.ReadAllText(f).Contains("# created by KnA", StringComparison.Ordinal)) File.Delete(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ Windows

    private static string SendToFolder => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static async Task InstallWindowsAsync(string exe, IReadOnlyList<Entry> entries)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var sb = new StringBuilder("$sh = New-Object -ComObject WScript.Shell\n");
        foreach (var e in entries)
        {
            var lnk = Path.Combine(SendToFolder, SafeFileName(e.Name) + ".lnk");
            sb.Append($"$s = $sh.CreateShortcut({Q(lnk)}); $s.TargetPath = {Q(exe)}; $s.Arguments = {Q(e.Arguments)}; ")
              .Append($"$s.IconLocation = {Q(exe + ",0")}; $s.WorkingDirectory = {Q(Path.GetDirectoryName(exe)!)}; ")
              .Append($"$s.Description = {Q(L.T("Add to the KnA queue"))}; $s.Save()\n");
        }
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
        using var p = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded])
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("powershell.exe could not be started.");
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException(err.Trim());
    }

    // ------------------------------------------------------------------ Linux

    private static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
        ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    private static string DesktopFile => Path.Combine(DataHome, "applications", "kna.desktop");
    private static string DolphinFile => Path.Combine(DataHome, "kio", "servicemenus", "kna.desktop");
    private static string NautilusDir => Path.Combine(DataHome, "nautilus", "scripts");

    private const string Mime = "video/x-matroska;video/mp4;video/x-msvideo;video/quicktime;video/webm;video/mp2t;video/mpeg;video/x-flv;video/x-ms-wmv;video/ogg;";

    private static string Exec(string exe, string args) => $"\"{exe}\" {args} %F".Replace("  ", " ");

    private static void InstallLinux(string exe, IReadOnlyList<Entry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopFile)!);
        var actions = entries.Skip(1).Select((e, i) => (Id: "preset" + i, e)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("[Desktop Entry]").AppendLine("Type=Application").AppendLine("Name=KnA - Konjiki no Autoencoder")
          .AppendLine("Comment=" + L.T("Add to the KnA queue")).AppendLine("Exec=" + Exec(exe, "")).AppendLine("Icon=video-x-generic")
          .AppendLine("Terminal=false").AppendLine("Categories=AudioVideo;Video;").AppendLine("MimeType=" + Mime);
        if (actions.Count > 0) sb.AppendLine("Actions=" + string.Join(";", actions.Select(a => a.Id)) + ";");
        foreach (var (id, e) in actions)
            sb.AppendLine().AppendLine($"[Desktop Action {id}]").AppendLine("Name=" + e.Name).AppendLine("Exec=" + Exec(exe, e.Arguments));
        File.WriteAllText(DesktopFile, sb.ToString());

        // Dolphin (KDE) right-click menu.
        Directory.CreateDirectory(Path.GetDirectoryName(DolphinFile)!);
        var kde = new StringBuilder();
        kde.AppendLine("[Desktop Entry]").AppendLine("Type=Service").AppendLine("MimeType=video/*;")
           .AppendLine("Actions=" + string.Join(";", entries.Select((_, i) => "kna" + i)) + ";").AppendLine("X-KDE-Submenu=KnA");
        for (int i = 0; i < entries.Count; i++)
            kde.AppendLine().AppendLine($"[Desktop Action kna{i}]").AppendLine("Name=" + entries[i].Name).AppendLine("Icon=video-x-generic").AppendLine("Exec=" + Exec(exe, entries[i].Arguments));
        File.WriteAllText(DolphinFile, kde.ToString());
        TryChmod(DolphinFile);

        // Nautilus (GNOME) scripts menu.
        Directory.CreateDirectory(NautilusDir);
        foreach (var e in entries)
        {
            var path = Path.Combine(NautilusDir, SafeFileName(e.Name));
            File.WriteAllText(path, $"#!/bin/sh\n# created by KnA\nexec \"{exe}\" {e.Arguments} \"$@\"\n");
            TryChmod(path);
        }
    }

    private static void TryChmod(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead); }
        catch (IOException) { }
    }
}
