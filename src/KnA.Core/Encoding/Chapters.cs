using System.Globalization;
using System.Text;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>
/// Chapter editing: the file's chapters, the user's list (<see cref="JobEdit.Chapters"/>) and the
/// FFmetadata file FFmpeg reads them from ("-f ffmetadata -i chapters.txt -map_chapters N").
/// </summary>
public static class Chapters
{
    /// <summary>The chapters of the source as editable marks.</summary>
    public static List<ChapterMark> FromSource(MediaInfo media) =>
        [.. media.Chapters.Select(c => new ChapterMark(c.Start, c.Title))];

    /// <summary>The chapters the output will get (source timeline), honouring KeepChapters and the edited list.</summary>
    public static List<ChapterMark> Effective(MediaInfo media, JobEdit edit) =>
        !edit.KeepChapters ? [] : edit.Chapters is { } list ? [.. list.OrderBy(c => c.Start)] : FromSource(media);

    /// <summary>
    /// Moves chapters from the source timeline onto an output made of the kept parts joined together;
    /// a chapter that falls in a removed part starts at the beginning of the next kept part.
    /// </summary>
    public static List<ChapterMark> Remap(IEnumerable<ChapterMark> chapters, IReadOnlyList<(double Start, double End)> parts)
    {
        if (parts.Count == 0) return [.. chapters];
        var result = new List<ChapterMark>();
        foreach (var c in chapters.OrderBy(c => c.Start))
        {
            double offset = 0;
            double? at = null;
            foreach (var (s, e) in parts)
            {
                if (c.Start < e - 0.0005) { at = offset + Math.Max(0, c.Start - s); break; }
                offset += e - s;
            }
            if (at is null) continue;
            // Several chapters collapsing on the same point: keep the last one (its title describes what follows).
            if (result.Count > 0 && Math.Abs(result[^1].Start - at.Value) < 0.001) result.RemoveAt(result.Count - 1);
            result.Add(new ChapterMark(at.Value, c.Title));
        }
        return result;
    }

    /// <summary>FFmetadata text (millisecond time base); each chapter ends where the next starts.</summary>
    public static string ToFfmetadata(IReadOnlyList<ChapterMark> chapters, double duration)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        var sorted = chapters.OrderBy(c => c.Start).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            long start = (long)Math.Round(Math.Max(0, sorted[i].Start) * 1000);
            double endSec = i + 1 < sorted.Count ? sorted[i + 1].Start : Math.Max(duration, sorted[i].Start + 1);
            long end = Math.Max(start + 1, (long)Math.Round(endSec * 1000));
            sb.Append("\n[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append("START=").Append(start.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("END=").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (!string.IsNullOrWhiteSpace(sorted[i].Title)) sb.Append("title=").Append(Escape(sorted[i].Title!)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Writes the FFmetadata file and returns its path.</summary>
    public static string Write(string path, IReadOnlyList<ChapterMark> chapters, double duration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToFfmetadata(chapters, duration), new UTF8Encoding(false));
        return path;
    }

    private static string Escape(string v)
    {
        var sb = new StringBuilder();
        foreach (var ch in v.Replace("\r", "").Replace("\n", " "))
        {
            if (ch is '=' or ';' or '#' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>"Chapter 01" style default title.</summary>
    public static string DefaultTitle(int number) => Localization.L.T("Chapter {0}", number.ToString("00", CultureInfo.InvariantCulture));
}
