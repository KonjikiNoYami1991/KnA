using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using KnA.Core.Localization;
using KnA.Core.Processing;
using KnA.Core.Tools;

namespace KnA.Core.Ocr;

/// <summary>How the subtitle pictures are read.</summary>
public sealed record OcrOptions
{
    /// <summary>"tesseract" or "paddleocr".</summary>
    public string Engine { get; init; } = "tesseract";
    /// <summary>Engine language code; null = from the track language.</summary>
    public string? Language { get; init; }
    /// <summary>Find italic lines from the slant of the letters (Tesseract 4/5 no longer reports font styles).</summary>
    public bool DetectItalic { get; init; } = true;
    /// <summary>Apply the usual OCR fixes (| → I, spaces before punctuation, dialogue dashes…).</summary>
    public bool FixCommonErrors { get; init; } = true;
}

/// <summary>One subtitle of an OCR session: its picture, the text read and what the user changed.</summary>
public sealed class OcrLine(int index, SubtitleImage image)
{
    public int Index { get; } = index;
    public SubtitleImage Image { get; } = image;
    public TimeSpan Start { get; set; } = image.Start;
    public TimeSpan End { get; set; } = image.End;
    public string Text { get; set; } = "";
    /// <summary>0–100 (mean of the words), -1 = not read yet.</summary>
    public double Confidence { get; set; } = -1;
    public bool Italic { get; set; }
    public bool Forced => Image.Forced;
    /// <summary>Changed by hand: a new OCR pass leaves it alone.</summary>
    public bool Edited { get; set; }
}

/// <summary>
/// Reads subtitle pictures with Tesseract 5 (LSTM models, confidence per word from its TSV output) or PaddleOCR
/// (PP-OCRv5, the standalone build Subtitle Edit uses), several pictures per process and several processes at once.
/// </summary>
public sealed partial class OcrEngine(string dataDirectory, string? tesseract)
{
    public string DisplayName(OcrOptions o) => o.Engine == "paddleocr" ? "PaddleOCR" : "Tesseract";

    public string LanguageFor(OcrOptions o, string? trackLanguage) =>
        o.Language ?? (o.Engine == "paddleocr" ? PaddleOcr.Language(trackLanguage) : SubtitleOcr.TesseractLanguage(trackLanguage));

    /// <summary>Reads every line not edited by hand; <paramref name="progress"/> counts finished lines.</summary>
    public async Task RecognizeAsync(IReadOnlyList<OcrLine> lines, OcrOptions o, string workDir, IProgress<int>? progress, Action<string>? log, CancellationToken ct)
    {
        var todo = lines.Where(l => !l.Edited).ToList();
        if (todo.Count == 0) return;
        Directory.CreateDirectory(workDir);
        try
        {
            if (o.Engine == "paddleocr") await PaddleOcr.RecognizeAsync(dataDirectory, todo, o.Language ?? "en", workDir, progress, ct);
            else await TesseractAsync(todo, o, workDir, progress, log, ct);
            foreach (var l in todo)
            {
                if (o.FixCommonErrors) l.Text = SubtitleOcr.Clean(l.Text);
                if (o.DetectItalic && l.Text.Length > 0) l.Italic = Italic.IsItalic(l.Image);
            }
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task TesseractAsync(List<OcrLine> todo, OcrOptions o, string dir, IProgress<int>? progress, Action<string>? log, CancellationToken ct)
    {
        var exe = tesseract is { Length: > 0 } ? tesseract : throw new InvalidOperationException(L.T("OCR needs Tesseract: install it in Settings → Components (Linux: apt install tesseract-ocr; macOS: brew install tesseract)."));
        var lang = o.Language ?? "eng";
        // "ita+eng": every model of the combination must be there
        string? models = null;
        foreach (var part in lang.Split('+'))
        {
            var found = SubtitleOcr.FindModel(dataDirectory, exe, part);
            if (found is null)
            {
                log?.Invoke(L.T("OCR: downloading the {0} language model…", part));
                found = await SubtitleOcr.DownloadModelAsync(dataDirectory, part, best: true, null, ct);
            }
            models ??= found;
            if (!File.Exists(Path.Combine(models, part + ".traineddata")))
                File.Copy(Path.Combine(found, part + ".traineddata"), Path.Combine(SubtitleOcr.ModelFolder(dataDirectory), part + ".traineddata"), true);
        }
        var files = new string[todo.Count];
        Parallel.For(0, todo.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            files[i] = Path.Combine(dir, $"{i:D5}.png");
            var (w, h, gray) = SubtitleOcr.Prepare(todo[i].Image);
            PngWriter.WriteGray(files[i], w, h, gray);
        });
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        int per = Math.Max(1, Math.Min(200, (todo.Count + workers - 1) / workers));
        int done = 0;
        var chunks = Enumerable.Range(0, (todo.Count + per - 1) / per).Select(c => (Start: c * per, Count: Math.Min(per, todo.Count - c * per)));
        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (chunk, token) =>
        {
            var list = Path.Combine(dir, $"list-{chunk.Start}.txt");
            await File.WriteAllLinesAsync(list, files.Skip(chunk.Start).Take(chunk.Count), token);
            // --psm 6: one block of text (a subtitle); --oem 1: LSTM; tsv: words with their confidence, page = picture
            var r = await ProcessRunner.RunAsync(exe, [list, "stdout", "-l", lang, "--psm", "6", "--oem", "1", "--tessdata-dir", models!, "tsv"],
                timeout: TimeSpan.FromHours(2), ct: token);
            if (r.ExitCode != 0 && string.IsNullOrWhiteSpace(r.StdOut))
                throw new InvalidOperationException("Tesseract: " + (r.StdErr.Trim() is { Length: > 0 } e ? e : $"exit {r.ExitCode}"));
            foreach (var (page, text, conf) in ParseTsv(r.StdOut))
                if (page >= 1 && page <= chunk.Count)
                {
                    var line = todo[chunk.Start + page - 1];
                    line.Text = text;
                    line.Confidence = conf;
                }
            progress?.Report(Interlocked.Add(ref done, chunk.Count));
        });
    }

    /// <summary>Tesseract TSV → (page, text with line breaks, mean word confidence) per page.</summary>
    public static IEnumerable<(int Page, string Text, double Confidence)> ParseTsv(string tsv)
    {
        var pages = new SortedDictionary<int, List<(int Block, int Par, int Line, string Word, double Conf)>>();
        foreach (var raw in tsv.Split('\n'))
        {
            var c = raw.TrimEnd('\r').Split('\t');
            if (c.Length < 12 || c[0] != "5") continue;   // level 5 = word
            if (!int.TryParse(c[1], out var page)) continue;
            double.TryParse(c[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf);
            var word = c[11];
            if (word.Trim().Length == 0) continue;
            if (!pages.TryGetValue(page, out var words)) pages[page] = words = [];
            words.Add((int.Parse(c[2], CultureInfo.InvariantCulture), int.Parse(c[3], CultureInfo.InvariantCulture), int.Parse(c[4], CultureInfo.InvariantCulture), word, conf));
        }
        foreach (var (page, words) in pages)
        {
            var text = string.Join('\n', words.GroupBy(w => (w.Block, w.Par, w.Line)).Select(g => string.Join(' ', g.Select(w => w.Word))));
            yield return (page, text, words.Count > 0 ? words.Average(w => Math.Max(0, w.Conf)) : 0);
        }
    }
}

/// <summary>
/// Italics from the picture: the letters are sheared back by a range of slopes and the slope that makes the
/// vertical strokes sharpest (highest column-projection energy) is the slant of the text. Upright text peaks
/// at 0, italic type at 0.15–0.35 (9–19°). Tesseract 4/5 dropped font-style detection; this does not need it.
/// </summary>
public static class Italic
{
    public static double Slant(SubtitleImage img)
    {
        int w = img.Width, h = img.Height;
        if (w < 8 || h < 8) return 0;
        // text fill: bright and opaque (the outline is dark, the background transparent)
        var ink = new List<(int X, int Y)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4;
                double luma = (0.2126 * img.Rgba[s] + 0.7152 * img.Rgba[s + 1] + 0.0722 * img.Rgba[s + 2]) / 255.0;
                if (luma * img.Rgba[s + 3] / 255.0 > 0.5) ink.Add((x, y));
            }
        if (ink.Count < 40) return 0;
        int pad = h;   // shear shifts by at most ~0.5 h
        double best = 0, bestScore = double.MinValue, upright = 0;
        var cols = new int[w + 2 * pad];
        for (double t = -0.15; t <= 0.45001; t += 0.025)
        {
            Array.Clear(cols);
            foreach (var (x, y) in ink)
            {
                int nx = x + pad + (int)Math.Round(t * (y - h / 2.0));   // straighten a right-leaning slant
                if (nx >= 0 && nx < cols.Length) cols[nx]++;
            }
            double score = 0;
            foreach (var c in cols) score += (double)c * c;
            if (Math.Abs(t) < 0.001) upright = score;
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return bestScore > upright * 1.04 ? best : 0;
    }

    public static bool IsItalic(SubtitleImage img) => Slant(img) >= 0.12;
}

/// <summary>
/// PaddleOCR (PP-OCRv5) through the standalone build Subtitle Edit uses (timminator/PaddleOCR-Standalone): the
/// pictures are written to a folder, read in one run, and the "ppocr INFO" lines give each text box with its score.
/// </summary>
public static partial class PaddleOcr
{
    public const string Version = "v1.4.0";
    private const string Base = "https://github.com/timminator/PaddleOCR-Standalone/releases/download/" + Version + "/";
    public static readonly string[] WindowsCpu = [Base + "PaddleOCR-CPU-v1.4.0.7z"];
    public static readonly string[] WindowsGpu = [Base + "PaddleOCR-GPU-v1.4.0-CUDA-12.9.7z"];
    public static readonly string[] LinuxCpu = [Base + "PaddleOCR-CPU-v1.4.0-Linux.7z"];
    public static readonly string[] LinuxGpu = [Base + "PaddleOCR-GPU-v1.4.0-CUDA-12.9-Linux.7z.001", Base + "PaddleOCR-GPU-v1.4.0-CUDA-12.9-Linux.7z.002"];
    public static readonly string[] Models = [Base + "PaddleOCR.PP-OCRv5.support.files.VideOCR.7z"];
    private const string Orientation = "PP-LCNet_x1_0_textline_ori";

    public static string Folder(string dataDirectory) => Path.Combine(Settings.AppPaths.Tools(dataDirectory), "paddleocr");
    public static string ModelFolder(string dataDirectory) => Path.Combine(Folder(dataDirectory), "models");

    public static string? Locate(string dataDirectory)
    {
        var dir = Folder(dataDirectory);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, OperatingSystem.IsWindows() ? "paddleocr.exe" : "paddleocr.bin", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.Contains(Path.DirectorySeparatorChar + "models" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    public static bool ModelsInstalled(string dataDirectory) =>
        Directory.Exists(Path.Combine(ModelFolder(dataDirectory), "det")) && Directory.Exists(Path.Combine(ModelFolder(dataDirectory), "rec"));

    private static readonly string[] Latin = ["af", "az", "bs", "cs", "cy", "da", "de", "es", "et", "fr", "ga", "hr", "hu", "id", "is", "it", "ku", "la",
        "lt", "lv", "mi", "ms", "mt", "nl", "no", "oc", "pi", "pl", "pt", "ro", "rs_latin", "sk", "sl", "sq", "sv", "sw", "tl", "tr", "uz", "vi", "french", "german"];
    private static readonly string[] Arabic = ["ar", "fa", "ug", "ur"];
    private static readonly string[] Eslav = ["ru", "be", "uk"];

    /// <summary>Track language → PaddleOCR language code.</summary>
    public static string Language(string? language) => SubtitleOcr.TesseractLanguage(language) switch
    {
        "eng" => "en", "ita" => "it", "fra" => "french", "deu" => "german", "spa" => "es", "por" => "pt", "nld" => "nl", "pol" => "pl",
        "ces" => "cs", "slk" => "sk", "hun" => "hu", "ron" => "ro", "tur" => "tr", "swe" => "sv", "dan" => "da", "nor" => "no", "fin" => "fi",
        "rus" => "ru", "ukr" => "uk", "jpn" => "japan", "chi_sim" => "ch", "kor" => "korean", "ara" => "ar", "fas" => "fa", "hrv" => "hr",
        "slv" => "sl", "vie" => "vi", "ind" => "id", "est" => "et", "lav" => "lv", "lit" => "lt",
        _ => "en",
    };

    private static string Pick(string root, string kind, params string[] names) =>
        names.FirstOrDefault(n => Directory.Exists(Path.Combine(root, kind, n))) ?? names[^1];

    public static async Task RecognizeAsync(string dataDirectory, List<OcrLine> todo, string lang, string dir, IProgress<int>? progress, CancellationToken ct)
    {
        var exe = Locate(dataDirectory) ?? throw new InvalidOperationException(L.T("PaddleOCR is not installed: Settings → Components → PaddleOCR."));
        if (!ModelsInstalled(dataDirectory)) throw new InvalidOperationException(L.T("The PaddleOCR models are not installed: Settings → Components → PaddleOCR models."));
        var models = ModelFolder(dataDirectory);
        bool v5 = lang is "ch" or "chinese_cht" or "en" or "japan";
        string det = lang is "ch" or "chinese_cht" or "en" or "japan" or "korean" || Latin.Contains(lang) || Eslav.Contains(lang)
            ? Pick(models, "det", "PP-OCRv5_server_det", "PP-OCRv5_mobile_det") : "PP-OCRv3_mobile_det";
        string rec = v5 ? Pick(models, "rec", "PP-OCRv5_server_rec", "PP-OCRv5_mobile_rec")
            : Arabic.Contains(lang) ? "arabic_PP-OCRv3_mobile_rec" : Eslav.Contains(lang) ? "eslav_PP-OCRv5_mobile_rec"
            : lang == "korean" ? "korean_PP-OCRv5_mobile_rec" : "latin_PP-OCRv5_mobile_rec";
        var input = Path.Combine(dir, "paddle");
        Directory.CreateDirectory(input);
        var byFile = new Dictionary<string, OcrLine>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < todo.Count; i++)
        {
            var file = Path.Combine(input, $"{i:D5}.png");
            var (w, h, gray) = Prepare(todo[i].Image);
            PngWriter.WriteGray(file, w, h, gray);
            byFile[Path.GetFileName(file)] = todo[i];
        }
        var results = new ConcurrentDictionary<OcrLine, List<(double X, double Y, double H, string Text, double Score)>>();
        OcrLine? current = null;
        int done = 0;
        var psi = ProcessRunner.CreateStartInfo(exe, ["ocr", "-i", input, "--use_textline_orientation", "true", "--use_doc_orientation_classify", "false",
            "--use_doc_unwarping", "false", "--lang", lang,
            "--text_detection_model_dir", Path.Combine(models, "det", det), "--text_detection_model_name", det,
            "--text_recognition_model_dir", Path.Combine(models, "rec", rec), "--text_recognition_model_name", rec,
            "--textline_orientation_model_dir", Path.Combine(models, "cls", Orientation), "--textline_orientation_model_name", Orientation], dir);
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        var err = new System.Text.StringBuilder();
        void OnLine(string? line)
        {
            if (line is null || !line.Contains("ppocr INFO:", StringComparison.Ordinal)) return;
            var hit = byFile.Keys.FirstOrDefault(f => line.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) { if (current is not null) progress?.Report(Interlocked.Increment(ref done)); current = byFile[hit]; return; }
            if (current is not null && ParseBox(line) is { } box) results.GetOrAdd(current, _ => []).Add(box);
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => { OnLine(e.Data); if (e.Data is { } d && err.Length < 4000) err.AppendLine(d); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => ProcessControl.KillTree(p))) await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new InvalidOperationException("PaddleOCR: " + err.ToString().Trim().Split('\n').LastOrDefault());
        foreach (var l in todo)
        {
            if (!results.TryGetValue(l, out var boxes) || boxes.Count == 0) { l.Text = ""; l.Confidence = 0; continue; }
            // boxes → lines (by vertical position), words left to right
            double hAvg = boxes.Average(b => b.H);
            var lines = new List<List<(double X, double Y, double H, string Text, double Score)>>();
            foreach (var b in boxes.OrderBy(b => b.Y))
                if (lines.Count > 0 && Math.Abs(lines[^1][0].Y - b.Y) < hAvg * 0.6) lines[^1].Add(b); else lines.Add([b]);
            l.Text = string.Join('\n', lines.Select(x => string.Join(' ', x.OrderBy(b => b.X).Select(b => b.Text))));
            l.Confidence = boxes.Average(b => b.Score) * 100;
        }
        progress?.Report(todo.Count);
    }

    /// <summary>PaddleOCR likes light text on dark: the letters keep their brightness, everything else is black.</summary>
    private static (int, int, byte[]) Prepare(SubtitleImage img)
    {
        const int margin = 20;
        int w = img.Width + 2 * margin, h = img.Height + 2 * margin;
        var gray = new byte[w * h];
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int s = (y * img.Width + x) * 4;
                double luma = 0.2126 * img.Rgba[s] + 0.7152 * img.Rgba[s + 1] + 0.0722 * img.Rgba[s + 2];
                gray[(y + margin) * w + x + margin] = (byte)Math.Clamp((int)(luma * img.Rgba[s + 3] / 255.0), 0, 255);
            }
        return (w, h, gray);
    }

    // [[[92.0, 56.0], [735.0, 60.0], [734.0, 118.0], [91.0, 113.0]], ('My mommy always said', 0.99)]
    [GeneratedRegex(@"\[\[\[([\d.]+),\s*([\d.]+)\],\s*\[[\d.]+,\s*[\d.]+\],\s*\[[\d.]+,\s*([\d.]+)\],\s*\[[\d.]+,\s*[\d.]+\]\],\s*\(['""](.*)['""],\s*([\d.]+)\)\]")]
    private static partial Regex Box();

    public static (double X, double Y, double H, string Text, double Score)? ParseBox(string line)
    {
        var m = Box().Match(line);
        if (!m.Success) return null;
        double D(int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        double top = D(2), bottom = D(3);
        return (D(1), (top + bottom) / 2, Math.Abs(bottom - top), m.Groups[4].Value.Replace("\\'", "'"), D(5));
    }
}
