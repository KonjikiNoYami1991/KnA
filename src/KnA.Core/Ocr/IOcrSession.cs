using KnA.Core.Queue;

namespace KnA.Core.Ocr;

/// <summary>The OCR editor's session: on this computer (<see cref="LocalOcrSession"/>) or on a KnA server.</summary>
public interface IOcrSession
{
    QueueJob Job { get; }
    string File { get; }
    int Stream { get; }
    string? Language { get; }
    List<OcrLine> Lines { get; }
    bool Busy { get; }
    bool ToCheck(OcrLine line);
    void Edit(int index, string? text = null, bool? italic = null, TimeSpan? start = null, TimeSpan? end = null);
    int Replace(string find, string replace, bool regex, bool matchCase);
    Task RecognizeAsync(OcrOptions options, IReadOnlyCollection<int>? only, IProgress<int>? progress, CancellationToken ct);
    /// <summary>The usual SRT name next to the source (a suggestion for "Save as").</summary>
    string DefaultSrtPath();
    /// <summary>Writes the lines as SRT on this computer.</summary>
    Task<int> SaveAsync(string srtPath, CancellationToken ct = default);
    /// <summary>The SRT next to the source, used in the job instead of the picture track.</summary>
    Task UseInJobAsync(CancellationToken ct = default);
    /// <summary>Remembers the engine and switches as the default of the machine that runs OCR.</summary>
    void RememberOptions(OcrOptions options);
}

public sealed class LocalOcrSession(QueueService queue, OcrSession session) : IOcrSession
{
    public QueueJob Job => session.Job;
    public string File => session.File;
    public int Stream => session.Stream;
    public string? Language => session.Language;
    public List<OcrLine> Lines => session.Lines;
    public bool Busy => session.Busy;
    public bool ToCheck(OcrLine line) => session.ToCheck(line);
    public void Edit(int index, string? text = null, bool? italic = null, TimeSpan? start = null, TimeSpan? end = null) => session.Edit(index, text, italic, start, end);
    public int Replace(string find, string replace, bool regex, bool matchCase) => session.Replace(find, replace, regex, matchCase);
    public Task RecognizeAsync(OcrOptions options, IReadOnlyCollection<int>? only, IProgress<int>? progress, CancellationToken ct) =>
        session.RecognizeAsync(queue, options, only, progress, ct);
    public string DefaultSrtPath() => session.DefaultSrtPath();
    public Task<int> SaveAsync(string srtPath, CancellationToken ct = default) => session.SaveAsync(srtPath, ct);
    public async Task UseInJobAsync(CancellationToken ct = default) => await session.UseInJobAsync(queue, session.DefaultSrtPath(), ct);
    public void RememberOptions(OcrOptions options)
    {
        queue.Settings.Ocr = options with { Language = null };
        queue.SaveSettings();
    }
}
