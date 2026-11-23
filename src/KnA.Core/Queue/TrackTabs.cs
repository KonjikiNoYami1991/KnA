using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;
using KnA.Core.Ocr;

namespace KnA.Core.Queue;

/// <summary>
/// The Audio and Subtitles tabs of the editor show one tab per active track (kept, or burned for subtitles): tracks
/// are added with "+", removed with ✕, ordered with ← → (= order in the output file) and duplicated (the same source
/// track twice with its own settings). Shared by the desktop editor; the web editor (editor.js) mirrors it.
/// </summary>
public static class TrackTabs
{
    /// <summary>The track has a tab: it is in the output (or burned into the picture).</summary>
    public static bool IsActive(TrackPlan t) => t.Include || t.Burn;

    /// <summary>Indexes (in the plan) of the active tracks of a kind, in output order.</summary>
    public static List<int> Active(IReadOnlyList<TrackPlan> list, string kind) =>
        list.Select((t, i) => (t, i)).Where(x => x.t.Kind == kind && IsActive(x.t)).Select(x => x.i).ToList();

    /// <summary>Indexes of the tracks of a kind that "+" can add (the file's and the added files' tracks not active yet).</summary>
    public static List<int> Inactive(IReadOnlyList<TrackPlan> list, string kind) =>
        list.Select((t, i) => (t, i)).Where(x => x.t.Kind == kind && !IsActive(x.t) && x.t.Copy == 0).Select(x => x.i).ToList();

    /// <summary>Short name of what the track becomes: "Opus", "AAC", "SRT", "copy"…</summary>
    public static string OutputLabel(TrackPlan t)
    {
        if (t.Kind == "subtitle")
        {
            if (t.Burn) return L.T("burned");
            return (t.Output ?? "copy").ToLowerInvariant() switch
            {
                "copy" => L.T("copy"),
                "srt" => "SRT",
                "ass" => "ASS",
                "webvtt" => "WebVTT",
                "mov_text" => "mov_text",
                SubtitleOcr.OutputValue => "OCR",
                var o => o,
            };
        }
        if (t.Kind != "audio") return t.Output ?? "";
        if (!Enum.TryParse<AudioCodec>(t.Output, true, out var c) || c == AudioCodec.Auto) return L.T("automatic");
        return Short(c);
    }

    public static string Short(AudioCodec c) => c switch
    {
        AudioCodec.Copy => L.T("copy"),
        AudioCodec.Aac => "AAC",
        AudioCodec.HeAac => "HE-AAC",
        AudioCodec.Ac3 => "AC-3",
        AudioCodec.Eac3 => "E-AC-3",
        AudioCodec.Flac => "FLAC",
        AudioCodec.Alac => "ALAC",
        AudioCodec.TrueHd => "TrueHD",
        AudioCodec.Dts => "DTS",
        AudioCodec.Mp3 => "MP3",
        AudioCodec.Pcm16 => "PCM 16",
        AudioCodec.Pcm24 => "PCM 24",
        AudioCodec.Pcm32Float => "PCM 32f",
        _ => c.ToString(),
    };

    /// <summary>Tab label: "#1 ita → Opus" (an added file's tracks carry the file name).</summary>
    public static string TabLabel(TrackPlan t, string sourcePath)
    {
        var s = (t.Stream >= 0 ? "#" + t.Stream : "") + (t.Language is { Length: > 0 } l ? " " + l : "");
        if (t.File != sourcePath) s += " · " + Path.GetFileName(t.File);
        s = s.Trim() + " → " + OutputLabel(t);
        return s.Length > 40 ? s[..39] + "…" : s;
    }

    /// <summary>Title written on a copy: "Italiano (Vorbis)".</summary>
    public static string CopyTitle(TrackPlan t)
    {
        var name = LanguageNames.NativeName(t.Language) ?? (string.IsNullOrWhiteSpace(t.Title) ? null : t.Title);
        var codec = OutputLabel(t);
        return name is null ? codec : $"{name} ({codec})";
    }

    /// <summary>
    /// A change of codec/format on a copy: when its title is still the one KnA wrote, it follows the new codec.
    /// </summary>
    public static TrackPlan Retitle(TrackPlan before, TrackPlan after) =>
        after.Copy > 0 && OutputLabel(before) != OutputLabel(after) && (before.Title is null || before.Title == CopyTitle(before))
            ? after with { Title = CopyTitle(after) } : after;

    /// <summary>
    /// Adds a copy of the track right after it (same settings, not default), titled with its codec; returns its index.
    /// </summary>
    public static int Duplicate(List<TrackPlan> list, int index)
    {
        var t = list[index];
        int copy = list.Where(x => x.File == t.File && x.Stream == t.Stream && x.Kind == t.Kind).Max(x => x.Copy) + 1;
        var dup = t with { Copy = copy, Include = true, Burn = false, Default = false };
        dup = dup with { Title = CopyTitle(dup) };
        list.Insert(index + 1, dup);
        return index + 1;
    }

    /// <summary>✕ on a tab: a copy is deleted, a track of the file is left out (it can come back with "+").</summary>
    public static void Remove(List<TrackPlan> list, int index)
    {
        if (list[index].Copy > 0) list.RemoveAt(index);
        else list[index] = list[index] with { Include = false, Burn = false };
    }

    /// <summary>"+": the track becomes active and goes after the last active track of its kind; returns its new index.</summary>
    public static int Activate(List<TrackPlan> list, int index)
    {
        var t = list[index] with { Include = true, Burn = false };
        list.RemoveAt(index);
        var active = Active(list, t.Kind);
        int at = active.Count > 0 ? active[^1] + 1 : Math.Min(index, list.Count);
        list.Insert(at, t);
        return at;
    }

    /// <summary>← →: swaps the track with the previous / next active track of its kind; returns its new index.</summary>
    public static int Move(List<TrackPlan> list, int index, int delta)
    {
        var active = Active(list, list[index].Kind);
        int pos = active.IndexOf(index) + delta;
        if (pos < 0 || pos >= active.Count) return index;
        int target = active[pos];
        (list[index], list[target]) = (list[target], list[index]);
        return target;
    }

    /// <summary>Burns this subtitle (at most one is burned); a burned track is not also kept as a track.</summary>
    public static void Burn(List<TrackPlan> list, int index)
    {
        for (int j = 0; j < list.Count; j++)
            if (list[j].Kind == "subtitle") list[j] = list[j] with { Burn = j == index, Include = j == index ? false : list[j].Include };
    }

    /// <summary>The job-wide audio filters (preset, assistant) merged into a track's own: the job's active values win.</summary>
    public static AudioFilterSettings MergeFilters(AudioFilterSettings? track, AudioFilterSettings job)
    {
        var f = track ?? new AudioFilterSettings();
        return f with
        {
            GainDb = Math.Abs(job.GainDb) > 0.01 ? job.GainDb : f.GainDb,
            Normalize = job.Normalize || f.Normalize,
            SampleRate = job.SampleRate ?? f.SampleRate,
            Custom = string.IsNullOrWhiteSpace(job.Custom) ? f.Custom : job.Custom,
            DownmixStereo = false,   // the track's channel count says it (Channels = 2)
        };
    }
}
