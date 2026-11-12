using TnA.Core.Media;

namespace TnA.Core.Encoding;

/// <summary>The subtitle chosen for burning in.</summary>
public sealed record SubtitleChoice(SubtitleStream? Stream, string? ExternalFile)
{
    public bool IsBitmap => Stream?.IsBitmap ?? false;
    public string Description => ExternalFile is not null
        ? $"external file {Path.GetFileName(ExternalFile)}"
        : $"track #{Stream!.Index} ({Stream.CodecName}{(Stream.Language is { } l ? ", " + l : "")}{(Stream.Title is { } t ? ", " + t : "")})";
}


public static class SubtitleSelector
{
    private static readonly string[] SidecarExtensions = [".ass", ".ssa", ".srt", ".vtt"];

    /// <summary>Sidecar file → forced → default → first text track → (same order) bitmap track.</summary>
    public static SubtitleChoice? Choose(MediaInfo media, bool useSidecar)
    {
        if (useSidecar && FindSidecar(media.Path) is { } file) return new SubtitleChoice(null, file);

        static SubtitleStream? Best(IEnumerable<SubtitleStream> s)
        {
            var list = s.ToList();
            return list.FirstOrDefault(x => x.IsForced) ?? list.FirstOrDefault(x => x.IsDefault) ?? list.FirstOrDefault();
        }

        var pick = Best(media.Subtitles.Where(s => s.IsText)) ?? Best(media.Subtitles.Where(s => s.IsBitmap));
        return pick is null ? null : new SubtitleChoice(pick, null);
    }

    /// <summary>Finds "Video.ass", "Video.ita.ass", … next to the video.</summary>
    public static string? FindSidecar(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (dir is null || !Directory.Exists(dir)) return null;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return Directory.EnumerateFiles(dir)
            .Where(f => SidecarExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f =>
            {
                var n = Path.GetFileNameWithoutExtension(f);
                return n.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                       || n.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => Array.IndexOf(SidecarExtensions, Path.GetExtension(f).ToLowerInvariant()))
            .ThenBy(f => f.Length)
            .FirstOrDefault();
    }
}
