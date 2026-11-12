using System.IO.Compression;
using System.Runtime.InteropServices;

namespace TnA.Core.Tools;

/// <summary>
/// Downloads a static FFmpeg build for Windows (BtbN "gpl" builds: libass, x264, x265, SVT-AV1,
/// NVENC, QSV, AMF). Plain .zip, so no 7-Zip dependency anymore.
/// On macOS/Linux the system package manager is the better source (brew / apt / jellyfin-ffmpeg).
/// </summary>
public static class FfmpegDownloader
{
    public static string? DownloadUrl => (OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture) switch
    {
        (true, Architecture.X64) => "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
        (true, Architecture.Arm64) => "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-winarm64-gpl.zip",
        _ => null,
    };

    public static string ManualInstructions =>
        OperatingSystem.IsMacOS() ? "Install Homebrew, then run: brew install ffmpeg"
        : OperatingSystem.IsLinux() ? "Install FFmpeg with your package manager (e.g. sudo apt install ffmpeg) or use jellyfin-ffmpeg for the best VA-API/QSV support. The Docker image already includes it."
        : "Download a build from https://github.com/BtbN/FFmpeg-Builds/releases and set its path in Settings.";

    /// <returns>Path of the installed ffmpeg executable.</returns>
    public static async Task<string> DownloadAsync(string dataDirectory, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var url = DownloadUrl ?? throw new PlatformNotSupportedException(ManualInstructions);
        var target = Path.Combine(dataDirectory, "ffmpeg");
        Directory.CreateDirectory(target);
        var zipPath = Path.Combine(Path.GetTempPath(), $"tna-ffmpeg-{Guid.NewGuid():N}.zip");

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("TnA-Autohardsubber/2.0");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total.Value * 100);
                }
            }

            using var zip = ZipFile.OpenRead(zipPath);
            string[] wanted = [FfmpegLocator.ExeName("ffmpeg"), FfmpegLocator.ExeName("ffprobe")];
            foreach (var entry in zip.Entries)
            {
                if (wanted.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                    || entry.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase))
                    entry.ExtractToFile(Path.Combine(target, entry.Name), overwrite: true);
            }
            var ffmpeg = Path.Combine(target, FfmpegLocator.ExeName("ffmpeg"));
            if (!File.Exists(ffmpeg)) throw new InvalidDataException("The archive did not contain ffmpeg.");
            return ffmpeg;
        }
        finally
        {
            try { File.Delete(zipPath); } catch (IOException) { }
        }
    }
}
