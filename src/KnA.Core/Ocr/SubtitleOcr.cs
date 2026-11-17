using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Processing;
using KnA.Core.Tools;

namespace KnA.Core.Ocr;

/// <summary>What an OCR run produced.</summary>
public sealed record OcrResult(string SrtPath, int Pictures, int Subtitles, string Language);

/// <summary>
/// Picture subtitles (Blu-ray PGS) → text subtitles (SRT), the way Subtitle Edit does it: the pictures are decoded
/// by KnA itself, prepared (dark text on white, margin) and read by Tesseract 5 with the language model of the track
/// (tessdata_best, downloaded on first use). Several Tesseract processes run side by side.
/// </summary>
public static partial class SubtitleOcr
{
    /// <summary>Official Tesseract models: "best" (LSTM, most accurate, what Subtitle Edit recommends) or "fast".</summary>
    public const string BestModels = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/main/";
    public const string FastModels = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/";
    /// <summary>Tesseract 5.5.0 for Windows: the same package Subtitle Edit downloads.</summary>
    public const string WindowsTesseractUrl = "https://github.com/SubtitleEdit/support-files/releases/download/tesseract550/Tesseract550.zip";

    public static string Folder(string dataDirectory) => Path.Combine(Settings.AppPaths.Tools(dataDirectory), "tesseract");
    public static string ModelFolder(string dataDirectory) => Path.Combine(Folder(dataDirectory), "tessdata");

    /// <summary>Track output value that asks for OCR (Subtitles tab).</summary>
    public const string OutputValue = "ocr";

    /// <summary>The track is to be turned into text: chosen on the track, or for every picture track by the settings.</summary>
    public static bool Wanted(Encoding.TrackPlan t, Encoding.JobSettings s) =>
        string.Equals(t.Output, OutputValue, StringComparison.OrdinalIgnoreCase) || (s.OcrPictureSubtitles && t.Output is null);

    /// <summary>Codecs that are pictures, not text (the ones this OCR reads).</summary>
    public static bool CanRead(string? codec) => codec is "hdmv_pgs_subtitle" or "pgssub" or "dvd_subtitle" or "dvdsub";

    /// <summary>tesseract(.exe): KnA's copy (any depth: the zip layout may change), next to the program, PATH, Homebrew.</summary>
    public static string? LocateTesseract(string dataDirectory)
    {
        if (Settings.AppEnv.Get("TESSERACT") is { } env && File.Exists(env)) return env;
        foreach (var root in new[] { Folder(dataDirectory), Path.Combine(AppContext.BaseDirectory, "tools", "tesseract") })
        {
            if (!Directory.Exists(root)) continue;
            var exe = Directory.EnumerateFiles(root, FfmpegLocator.ExeName("tesseract"), SearchOption.AllDirectories).FirstOrDefault();
            if (exe is not null) return exe;
        }
        return FfmpegLocator.LocateTool(["tesseract"], dataDirectory);
    }

    /// <summary>Folders searched for a model: KnA's, the bundled one, then the tessdata of a system installation.</summary>
    public static IEnumerable<string> ModelFolders(string dataDirectory, string? tesseract)
    {
        yield return ModelFolder(dataDirectory);
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "tesseract", "tessdata");
        if (tesseract is not null && Path.GetDirectoryName(tesseract) is { } dir) yield return Path.Combine(dir, "tessdata");
        if ((Settings.AppEnv.Get("TESSDATA_PREFIX") ?? Environment.GetEnvironmentVariable("TESSDATA_PREFIX")) is { Length: > 0 } prefix) yield return prefix;
        foreach (var sys in new[] { "/usr/share/tesseract-ocr/5/tessdata", "/usr/share/tesseract-ocr/4.00/tessdata", "/usr/share/tessdata",
                     "/opt/homebrew/share/tessdata", "/usr/local/share/tessdata" })
            yield return sys;
    }

    /// <summary>Folder holding <paramref name="lang"/>.traineddata, or null.</summary>
    public static string? FindModel(string dataDirectory, string? tesseract, string lang) =>
        ModelFolders(dataDirectory, tesseract).FirstOrDefault(d => File.Exists(Path.Combine(d, lang + ".traineddata")));

    /// <summary>Downloads the model of <paramref name="lang"/> into KnA's model folder (official tessdata repository).</summary>
    public static async Task<string> DownloadModelAsync(string dataDirectory, string lang, bool best, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = ModelFolder(dataDirectory);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, lang + ".traineddata");
        var tmp = target + ".part";
        await GitHubReleases.DownloadAsync((best ? BestModels : FastModels) + lang + ".traineddata", tmp, progress, ct);
        File.Move(tmp, target, overwrite: true);
        return dir;
    }

    /// <summary>Track language (ISO 639-2, both forms, or 639-1) → Tesseract model name.</summary>
    public static string TesseractLanguage(string? language) => (language ?? "").Trim().ToLowerInvariant() switch
    {
        "it" or "ita" => "ita", "en" or "eng" or "" or "und" => "eng",
        "fr" or "fre" or "fra" => "fra", "de" or "ger" or "deu" => "deu", "es" or "spa" => "spa", "pt" or "por" => "por",
        "nl" or "dut" or "nld" => "nld", "ru" or "rus" => "rus", "pl" or "pol" => "pol", "cs" or "cze" or "ces" => "ces",
        "sk" or "slo" or "slk" => "slk", "hu" or "hun" => "hun", "ro" or "rum" or "ron" => "ron", "el" or "gre" or "ell" => "ell",
        "tr" or "tur" => "tur", "sv" or "swe" => "swe", "da" or "dan" => "dan", "no" or "nor" or "nob" => "nor", "fi" or "fin" => "fin",
        "ja" or "jpn" => "jpn", "zh" or "chi" or "zho" => "chi_sim", "ko" or "kor" => "kor", "ar" or "ara" => "ara",
        "he" or "heb" => "heb", "hi" or "hin" => "hin", "th" or "tha" => "tha", "uk" or "ukr" => "ukr", "bg" or "bul" => "bul",
        "hr" or "hrv" => "hrv", "sr" or "srp" => "srp", "sl" or "slv" => "slv", "ca" or "cat" => "cat", "vi" or "vie" => "vie",
        "id" or "ind" => "ind", "ms" or "may" or "msa" => "msa", "fa" or "per" or "fas" => "fas", "et" or "est" => "est",
        "lv" or "lav" => "lav", "lt" or "lit" => "lit", "is" or "ice" or "isl" => "isl", "ga" or "gle" => "gle",
        var other when other.Length == 3 && other.All(char.IsLetter) => other,
        _ => "eng",
    };

    /// <summary>
    /// The pictures of one subtitle track: PGS through a .sup stream copy, DVD (VobSub) through FFprobe's packets.
    /// </summary>
    public static async Task<List<SubtitleImage>> LoadImagesAsync(string ffmpeg, string ffprobe, string source, int streamIndex, string? codec,
        string workDir, CancellationToken ct)
    {
        if (codec is "dvd_subtitle" or "dvdsub")
        {
            var (packets, header) = await VobSubParser.ReadAsync(ffprobe, source, streamIndex, ct);
            return VobSubParser.Parse(packets, header);
        }
        Directory.CreateDirectory(workDir);
        var sup = Path.Combine(workDir, $"track-{streamIndex}-{Guid.NewGuid().ToString("N")[..6]}.sup");
        try
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y" };
            args.AddRange(MediaProber.RobustInputArgs(source));
            args.AddRange(Media.Discs.DiscSource.InputArgs(source));
            args.AddRange(["-map", $"0:{streamIndex.ToString(CultureInfo.InvariantCulture)}", "-c:s", "copy", "-f", "sup", sup]);
            var r = await ProcessRunner.RunAsync(ffmpeg, args, timeout: TimeSpan.FromMinutes(30), ct: ct);
            if (r.ExitCode != 0 || !File.Exists(sup)) throw new InvalidOperationException(L.T("OCR: the subtitle track could not be extracted: {0}", r.StdErr.Trim()));
            return PgsParser.Parse(sup);
        }
        finally { try { File.Delete(sup); } catch (IOException) { } }
    }

    /// <summary>FFprobe next to FFmpeg (every KnA layout ships them together), else from the PATH.</summary>
    public static string FfprobeFor(string ffmpeg)
    {
        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", FfmpegLocator.ExeName("ffprobe"));
        return File.Exists(probe) ? probe : "ffprobe";
    }

    /// <summary>
    /// OCR of one picture subtitle track (PGS or DVD) of <paramref name="source"/> into <paramref name="srtPath"/>.
    /// Models are downloaded when missing; <paramref name="log"/> receives what happens.
    /// </summary>
    public static async Task<OcrResult> RecognizeAsync(string ffmpeg, string tesseract, string dataDirectory, string source, int streamIndex,
        string? language, string srtPath, string workDir, IProgress<double>? progress = null, Action<string>? log = null, CancellationToken ct = default,
        string? codec = null, OcrOptions? options = null)
    {
        options ??= new OcrOptions();
        var images = await LoadImagesAsync(ffmpeg, FfprobeFor(ffmpeg), source, streamIndex, codec, workDir, ct);
        var lines = images.Select((img, i) => new OcrLine(i, img)).ToList();
        var engine = new OcrEngine(dataDirectory, tesseract);
        var lang = engine.LanguageFor(options, language);
        log?.Invoke(L.T("OCR: {0} subtitle pictures, language {1}, {2}.", images.Count, lang, engine.DisplayName(options)));
        await engine.RecognizeAsync(lines, options with { Language = lang }, Path.Combine(workDir, $"ocr-{streamIndex}"),
            progress is null ? null : new Progress<int>(n => progress.Report(n * 100.0 / Math.Max(1, lines.Count))), log, ct);
        int n = await WriteSrtAsync(lines, srtPath, ct);
        log?.Invoke(L.T("OCR: {0} subtitles written to {1}.", n, Path.GetFileName(srtPath)));
        return new OcrResult(srtPath, images.Count, n, lang);
    }

    /// <summary>The lines with text as an SRT file (italics as &lt;i&gt;); returns how many were written.</summary>
    public static async Task<int> WriteSrtAsync(IEnumerable<OcrLine> lines, string srtPath, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var l in lines.OrderBy(l => l.Start))
        {
            var text = l.Text.Trim();
            if (text.Length == 0) continue;
            if (l.Italic && !text.Contains("<i>", StringComparison.Ordinal))
                text = string.Join('\n', text.Split('\n').Select(x => "<i>" + x + "</i>"));
            n++;
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append('\n')
              .Append(Stamp(l.Start)).Append(" --> ").Append(Stamp(l.End)).Append('\n')
              .Append(text).Append("\n\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(srtPath))!);
        await File.WriteAllTextAsync(srtPath, sb.ToString(), new UTF8Encoding(false), ct);
        return n;
    }

    /// <summary>
    /// Subtitle picture → what Tesseract reads best: dark text on white. Bright, opaque pixels (the letters) become
    /// black; the dark outline and the transparent background become white. A margin is added; small pictures (DVD
    /// sizes) are enlarged ×2.
    /// </summary>
    public static (int Width, int Height, byte[] Gray) Prepare(SubtitleImage img)
    {
        const int margin = 12;
        int scale = img.Height < 60 ? 2 : 1;
        int w = img.Width * scale + 2 * margin, h = img.Height * scale + 2 * margin;
        var gray = new byte[w * h];
        Array.Fill(gray, (byte)255);
        for (int y = 0; y < img.Height * scale; y++)
            for (int x = 0; x < img.Width * scale; x++)
            {
                int s = ((y / scale) * img.Width + (x / scale)) * 4;
                double luma = 0.2126 * img.Rgba[s] + 0.7152 * img.Rgba[s + 1] + 0.0722 * img.Rgba[s + 2];
                double ink = luma / 255.0 * (img.Rgba[s + 3] / 255.0);
                gray[(y + margin) * w + x + margin] = (byte)Math.Clamp(255 - (int)(ink * 255), 0, 255);
            }
        return (w, h, gray);
    }

    /// <summary>Common OCR slips that are never right in subtitles (Subtitle Edit fixes the same ones).</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r", "").Split('\n')
            .Select(l => Spaces().Replace(l, " ").Trim())
            .Select(l => l.Replace('|', 'I').Replace("‘‘", "\"").Replace("’’", "\"").Replace(" ,", ",").Replace(" .", ".")
                          .Replace(" !", "!").Replace(" ?", "?").Replace(" :", ":").Replace(" ;", ";"))
            .Select(l => DashStart().Replace(l, "- "))
            .Where(l => l.Any(char.IsLetterOrDigit));
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^[-–—]\s*")] private static partial Regex DashStart();

    public static string Stamp(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
}
