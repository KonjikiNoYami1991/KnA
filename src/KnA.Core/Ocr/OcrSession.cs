using System.Text.RegularExpressions;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Queue;

namespace KnA.Core.Ocr;

/// <summary>
/// The OCR editor's working state for one picture subtitle track (Subtitle Edit's "OCR" window): every picture with
/// the text read, its confidence and italics, edited by hand, read again in part, saved as SRT or put in the job.
/// </summary>
public sealed class OcrSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public required QueueJob Job { get; init; }
    public required string File { get; init; }
    public required int Stream { get; init; }
    /// <summary>Which copy of the track the text replaces when used in the job (<see cref="Encoding.TrackPlan.Copy"/>).</summary>
    public int Copy { get; init; }
    public string? Codec { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public required List<OcrLine> Lines { get; init; }
    /// <summary>A recognition run is going on.</summary>
    public bool Busy { get; private set; }
    public int Done { get; private set; }

    /// <summary>Below this confidence a line is "to check" (shown in orange/red).</summary>
    public const double CheckBelow = 85;

    public static async Task<OcrSession> OpenAsync(QueueService q, QueueJob job, string file, int stream, CancellationToken ct = default, int copy = 0)
    {
        var tools = q.Tools ?? throw new InvalidOperationException(L.T("FFmpeg was not found. Configure it in Settings."));
        var media = file == job.SourcePath ? job.Media : job.ExternalMedia.GetValueOrDefault(file);
        var st = media?.Subtitles.FirstOrDefault(s => s.Index == stream);
        var work = Path.Combine(q.Store.TempDirectory, "ocr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var images = await SubtitleOcr.LoadImagesAsync(tools.Ffmpeg, tools.Ffprobe, file, stream, st?.CodecName, work, ct);
            return new OcrSession
            {
                Job = job, File = file, Stream = stream, Copy = copy, Codec = st?.CodecName, Language = st?.Language, Title = st?.Title,
                Lines = images.Select((img, i) => new OcrLine(i, img)).ToList(),
            };
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }

    /// <summary>Reads all lines, or only <paramref name="only"/> (hand-edited lines are kept unless listed).</summary>
    public async Task RecognizeAsync(QueueService q, OcrOptions options, IReadOnlyCollection<int>? only, IProgress<int>? progress, CancellationToken ct)
    {
        var lines = only is null ? Lines : Lines.Where(l => only.Contains(l.Index)).ToList();
        if (only is not null) foreach (var l in lines) l.Edited = false;
        var engine = new OcrEngine(q.Store.DataDirectory, SubtitleOcr.LocateTesseract(q.Store.DataDirectory));
        var opt = options with { Language = options.Language is { Length: > 0 } ? options.Language : engine.LanguageFor(options, Language) };
        Busy = true;
        Done = 0;
        try
        {
            await engine.RecognizeAsync(lines, opt, Path.Combine(q.Store.TempDirectory, "ocr-" + Id.ToString("N")[..8]),
                new Progress<int>(n => { Done = n; progress?.Report(n); }), null, ct);
        }
        finally { Busy = false; }
    }

    public OcrLine? Line(int index) => index >= 0 && index < Lines.Count ? Lines[index] : null;

    /// <summary>Text changed by hand: kept by later OCR passes.</summary>
    public void Edit(int index, string? text = null, bool? italic = null, TimeSpan? start = null, TimeSpan? end = null)
    {
        if (Line(index) is not { } l) return;
        if (text is not null) { l.Text = text.Replace("\r", ""); l.Edited = true; }
        if (italic is { } it) { l.Italic = it; l.Edited = true; }
        if (start is { } s) l.Start = s;
        if (end is { } e) l.End = e;
    }

    /// <summary>Find and replace in every line (plain text or regular expression); returns how many lines changed.</summary>
    public int Replace(string find, string replace, bool regex, bool matchCase)
    {
        if (string.IsNullOrEmpty(find)) return 0;
        int n = 0;
        var rx = regex ? new Regex(find, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)) : null;
        foreach (var l in Lines)
        {
            var t = rx is not null ? rx.Replace(l.Text, replace)
                : l.Text.Replace(find, replace, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (t != l.Text) { l.Text = t; l.Edited = true; n++; }
        }
        return n;
    }

    /// <summary>Lines worth a look: nothing read, or a confidence below <see cref="CheckBelow"/>.</summary>
    public bool ToCheck(OcrLine l) => l.Confidence >= 0 && (l.Text.Trim().Length == 0 || l.Confidence < CheckBelow);

    /// <summary>The picture as PNG (web editor).</summary>
    public byte[] Png(int index) => Line(index) is { } l ? PngWriter.Rgba(l.Image.Width, l.Image.Height, l.Image.Rgba) : [];

    /// <summary>Default file name: next to the video, "name.lang.srt".</summary>
    public string DefaultSrtPath() =>
        Path.Combine(Path.GetDirectoryName(Job.SourcePath) ?? ".", Path.GetFileNameWithoutExtension(Job.SourcePath)
            + (Language is { Length: > 0 } l ? "." + l : "") + (Lines.Count > 0 && Lines.All(x => x.Forced) ? ".forced" : "") + ".srt");

    public async Task<int> SaveAsync(string srtPath, CancellationToken ct = default) => await SubtitleOcr.WriteSrtAsync(Lines, srtPath, ct);

    /// <summary>
    /// Saves the text and puts it in the job in place of the picture track (same language, title and flags; the
    /// picture track is left out but stays in the list).
    /// </summary>
    public async Task<JobAnalysis> UseInJobAsync(QueueService q, string srtPath, CancellationToken ct = default)
    {
        await SaveAsync(srtPath, ct);
        var a = await q.AnalyzeAsync(Job, ct);
        var tracks = (Job.Edit.Tracks ?? a.Tracks).ToList();
        int at = tracks.FindIndex(t => t.Kind == "subtitle" && t.File == File && t.Stream == Stream && t.Copy == Copy);
        var original = at >= 0 ? tracks[at] : null;
        var probe = await new MediaProber(q.Tools!.Ffprobe).ProbeAsync(srtPath, ct, requireVideo: false);
        var st = probe.Subtitles.FirstOrDefault() ?? throw new InvalidOperationException(L.T("The SRT file could not be read."));
        var text = TrackPlanner.FromStream(srtPath, st, include: original?.Include ?? true) with
        {
            Language = original?.Language ?? Language, Title = original?.Title ?? Title, Default = original?.Default ?? false,
            Forced = original?.Forced ?? false, DelayMs = original?.DelayMs ?? 0, Output = null,
        };
        tracks.RemoveAll(t => t.File == srtPath);
        at = original is null ? -1 : tracks.IndexOf(original);
        // A duplicate of the picture track is replaced by the text; the track itself stays in the list, left out.
        if (original is { Copy: > 0 }) tracks[at] = text;
        else if (original is not null) { tracks[at] = original with { Include = false, Burn = false, Output = null }; tracks.Insert(at + 1, text); }
        else tracks.Add(text);
        var edit = Job.Edit with { ExternalFiles = [.. Job.Edit.ExternalFiles.Where(f => f != srtPath), srtPath], Tracks = tracks };
        return await q.SetEditAsync(Job.Id, edit, ct);
    }
}
