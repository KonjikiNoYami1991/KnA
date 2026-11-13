using System.Globalization;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

public sealed record DemuxOutput(int Stream, string Kind, string Codec, string FileName);

public sealed record DemuxPlan(EncodePlan Plan, string Folder, IReadOnlyList<DemuxOutput> Outputs, bool DumpAttachments, IReadOnlyList<ValidationIssue> Issues);

/// <summary>
/// Extracts streams to separate files in a single FFmpeg run (one output per stream, all "-c copy").
/// Raw mode writes elementary streams with their natural extension; otherwise Matroska wrappers
/// (.mkv/.mka/.mks) keep timestamps exact (VFR video, delays). Text subtitles are always plain files.
/// Attachments are dumped with -dump_attachment, chapters with the ffmetadata muxer.
/// </summary>
public static class DemuxBuilder
{
    public static DemuxPlan Build(MediaInfo media, JobSettings s, JobEdit edit, BuildOptions o, string? forcedFolder = null)
    {
        var d = s.Demux;
        var folder = forcedFolder ?? OutputNaming.Folder(media.Path, "demux", edit.OutputFolder ?? o.OutputDirectory);
        var baseName = OutputNaming.Safe(Path.GetFileNameWithoutExtension(media.Path));
        var issues = new List<ValidationIssue>();
        var selected = d.Streams is { Count: > 0 } list ? list.ToHashSet() : null;

        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-ignore_unknown" };
        bool dumpAttachments = d.Attachments && media.Attachments.Count > 0 && (selected is null || media.Attachments.Any(x => selected.Contains(x.Index)));
        if (dumpAttachments) a.AddRange(["-dump_attachment:t", ""]);
        a.AddRange(MediaProber.RobustInputArgs(media.Path));
        a.AddRange(["-i", media.Path]);

        var trim = CommandBuilder.TrimArgs(edit, media.Duration, issues);
        var outputs = new List<DemuxOutput>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var st in media.AllStreams.Where(x => x is not AttachmentStream))
        {
            if (selected is not null && !selected.Contains(st.Index)) continue;
            if (st is VideoStream { IsAttachedPicture: true }) continue;
            var (ext, extra) = Extension(st, d.Raw);
            var name = $"{baseName}.{st.Index.ToString(CultureInfo.InvariantCulture)}";
            if (!string.IsNullOrWhiteSpace(st.Language) && st.Language != "und") name += "." + st.Language;
            if (!string.IsNullOrWhiteSpace(st.Title)) name += "." + OutputNaming.Safe(st.Title!);
            var file = name + ext;
            for (int i = 2; !used.Add(file); i++) file = $"{name} ({i}){ext}";

            a.AddRange(["-map", $"0:{st.Index}"]);
            a.AddRange(extra);
            if (trim is { } t) a.AddRange(t.Args);
            a.AddRange(["-map_metadata", "-1", Path.Combine(folder, file)]);
            outputs.Add(new DemuxOutput(st.Index, st.Kind, st.CodecName, file));
        }

        if (d.Chapters && media.ChapterCount > 0)
        {
            int any = media.AllStreams.First().Index;
            a.AddRange(["-map_chapters", "0", "-map", $"0:{any}", "-c", "copy", "-f", "ffmetadata", Path.Combine(folder, baseName + ".chapters.txt")]);
            outputs.Add(new DemuxOutput(-1, "chapters", "ffmetadata", baseName + ".chapters.txt"));
        }

        if (outputs.Count == 0 && !dumpAttachments)
            issues.Add(Containers.Error("Nothing to extract: select at least one stream."));
        if (outputs.Count == 0 && dumpAttachments)
        {
            // FFmpeg needs an output: write nothing useful to the null muxer.
            a.AddRange(["-t", "0", "-f", "null", "-"]);
        }
        return new DemuxPlan(new EncodePlan(a, folder), folder, outputs, dumpAttachments, issues);
    }

    /// <summary>File extension and extra options to write this stream alone.</summary>
    public static (string Ext, List<string> Args) Extension(StreamBase st, bool raw)
    {
        var c = st.CodecName;
        List<string> copy = ["-c", "copy"];
        switch (st.Kind)
        {
            case "video":
                if (!raw) return (".mkv", copy);
                return c switch
                {
                    "h264" => (".h264", copy),
                    "hevc" => (".hevc", copy),
                    "av1" => (".obu", copy),
                    "vp8" or "vp9" => (".ivf", copy),
                    "mpeg2video" or "mpeg1video" => (".m2v", copy),
                    "mpeg4" => (".m4v", copy),
                    _ => (".mkv", copy),
                };
            case "audio":
                if (!raw) return (".mka", copy);
                return c switch
                {
                    "aac" => (".aac", copy),
                    "ac3" => (".ac3", copy),
                    "eac3" => (".eac3", copy),
                    "dts" => (".dts", copy),
                    "truehd" => (".thd", copy),
                    "flac" => (".flac", copy),
                    "opus" => (".opus", copy),
                    "vorbis" => (".ogg", copy),
                    "mp3" => (".mp3", copy),
                    "mp2" => (".mp2", copy),
                    "alac" => (".m4a", copy),
                    "wavpack" => (".wv", copy),
                    var pcm when pcm.StartsWith("pcm_", StringComparison.Ordinal) => (".wav", copy),
                    _ => (".mka", copy),
                };
            case "subtitle":
                return c switch
                {
                    "ass" or "ssa" => (".ass", copy),
                    "subrip" or "srt" => (".srt", copy),
                    "webvtt" => (".vtt", copy),
                    "mov_text" or "text" => (".srt", ["-c", "srt"]),
                    "hdmv_pgs_subtitle" when raw => (".sup", copy),
                    _ => (".mks", copy),
                };
            default:
                return (".mkv", copy);
        }
    }

    public static string Describe(DemuxOutput o) => o.Kind == "chapters" ? L.T("chapters") : $"#{o.Stream} {o.Kind} {o.Codec}";
}
