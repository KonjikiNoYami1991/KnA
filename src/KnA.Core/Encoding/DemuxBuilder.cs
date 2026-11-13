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
    public static DemuxPlan Build(MediaInfo media, JobSettings s, JobEdit edit, BuildOptions o, string? forcedFolder = null, string? chaptersFile = null)
    {
        var d = s.Demux;
        var folder = forcedFolder ?? OutputNaming.Folder(media.Path, "demux" + (edit.PartLabel is { Length: > 0 } pl ? " " + pl : ""), edit.OutputFolder ?? o.OutputDirectory);
        var baseName = OutputNaming.Safe(Path.GetFileNameWithoutExtension(media.Path));
        var issues = new List<ValidationIssue>();
        // The track list decides (all streams by default, or the "Streams to extract" of the preset).
        var plan = edit.Tracks is { } custom ? TrackPlanner.Complete(custom, media) : TrackPlanner.Default(media, s);
        var selected = plan.Where(t => t.Include && t.File == media.Path && t.Stream >= 0).Select(t => t.Stream).ToHashSet();
        var order = plan.Where(t => t.File == media.Path && t.Stream >= 0).Select(t => t.Stream).ToList();

        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-ignore_unknown" };
        var attachments = media.Attachments.Where(x => selected.Contains(x.Index)).ToList();
        bool dumpAttachments = attachments.Count > 0;
        if (attachments.Count == media.Attachments.Count && dumpAttachments) a.AddRange(["-dump_attachment:t", ""]);
        else foreach (var at in attachments) a.AddRange([$"-dump_attachment:{at.Index.ToString(CultureInfo.InvariantCulture)}", ""]);
        a.AddRange(MediaProber.RobustInputArgs(media.Path));
        a.AddRange(KnA.Core.Media.Discs.DiscSource.InputArgs(media.Path));
        bool customChapters = chaptersFile is not null && edit.KeepChapters && edit.Chapters is { Count: > 0 };
        if (customChapters) a.AddRange(["-f", "ffmetadata", "-i", chaptersFile!]);

        if (edit.IsMultiSegment) issues.Add(Containers.Error("Several cut segments can't be joined in this profile: split them into separate jobs."));
        var trim = CommandBuilder.TrimArgs(edit, media.Duration, issues);
        var outputs = new List<DemuxOutput>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var streams = media.AllStreams.Where(x => x is not AttachmentStream)
            .OrderBy(x => order.IndexOf(x.Index) is >= 0 and var i ? i : int.MaxValue).ThenBy(x => x.Index);
        foreach (var st in streams)
        {
            if (!selected.Contains(st.Index)) continue;
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

        bool wantChapters = d.Chapters && edit.KeepChapters && (customChapters || (edit.Chapters is null && media.ChapterCount > 0));
        if (wantChapters && media.AllStreams.FirstOrDefault() is { } any)
        {
            a.AddRange(["-map_chapters", customChapters ? "1" : "0", "-map", $"0:{any.Index}", "-c", "copy", "-f", "ffmetadata", Path.Combine(folder, baseName + ".chapters.txt")]);
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
