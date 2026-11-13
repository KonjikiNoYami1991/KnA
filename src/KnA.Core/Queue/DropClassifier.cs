using System.Text.Json;
using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media.Discs;
using KnA.Core.Settings;

namespace KnA.Core.Queue;

public enum DropKind { Video, Disc, External, Preset, Folder, Duplicate, Rejected }

/// <summary>One dropped item and what KnA will do with it.</summary>
public sealed record DropItem(string Path, DropKind Kind, string? Reason = null)
{
    /// <summary>For external files: the job (already queued) or the dropped video it belongs to.</summary>
    public Guid? TargetJob { get; init; }
    public string? TargetVideo { get; init; }
    /// <summary>For folders: what was found inside.</summary>
    public int Videos { get; init; }
    public int Discs { get; init; }
}

public sealed record DropPlan(IReadOnlyList<DropItem> Items)
{
    public int Count(DropKind k) => Items.Count(i => i.Kind == k);
    public bool HasWork => Items.Any(i => i.Kind is DropKind.Video or DropKind.Disc or DropKind.External or DropKind.Preset or DropKind.Folder);
    /// <summary>Items to show before doing anything (big folders, rejects, duplicates).</summary>
    public bool NeedsConfirmation => Items.Any(i => i.Kind is DropKind.Rejected or DropKind.Duplicate)
                                     || Items.Where(i => i.Kind == DropKind.Folder).Sum(i => i.Videos + i.Discs) > 50;
}

public sealed record DropResult(int Added, int Attached, int Presets, IReadOnlyList<string> Messages);

/// <summary>
/// "Smart" drag and drop, shared by the desktop app and the web UI:
///  • videos, scripts, Blu-ray/DVD folders and .iso images → queue (duplicates of waiting jobs are skipped);
///  • subtitles / audio / fonts → attached to the job they belong to: the job they were dropped on,
///    or the queued/dropped video whose name they start with ("Ep01.ita.ass" → "Ep01.mkv");
///  • KnA preset files (.json) → imported;
///  • folders → scanned (videos + discs), big ones are confirmed first;
///  • anything else → rejected with the reason.
/// </summary>
public static class DropClassifier
{
    public static readonly string[] SubtitleExtensions = [".ass", ".ssa", ".srt", ".vtt", ".sup", ".sub", ".idx", ".smi", ".ttml"];
    public static readonly string[] AudioExtensions = [".mka", ".aac", ".ac3", ".eac3", ".ec3", ".dts", ".dtshd", ".thd", ".mlp", ".flac", ".opus", ".ogg", ".mp3", ".wav", ".w64", ".m4a", ".wv", ".mp2", ".alac", ".aiff", ".aif"];

    public static bool IsExternal(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return SubtitleExtensions.Contains(e) || AudioExtensions.Contains(e) || TrackPlanner.IsFontFile(path) || e == ".mks";
    }

    public static DropPlan Classify(IEnumerable<string> paths, QueueService queue, Guid? targetJob = null)
    {
        var items = new List<DropItem>();
        var list = paths.Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).Distinct().ToList();
        var waiting = queue.Jobs.Where(j => j.Status is JobStatus.Ready or JobStatus.Running or JobStatus.Paused)
            .Select(j => j.SourcePath).ToHashSet(StringComparer.Ordinal);
        var droppedVideos = new List<string>();

        foreach (var p in list.Where(p => !IsExternal(p)))
        {
            if (!File.Exists(p) && !Directory.Exists(p) && !DiscSource.IsTitlePath(p))
            {
                items.Add(new DropItem(p, DropKind.Rejected, L.T("Not found.")));
                continue;
            }
            if (DiscSource.Detect(p) is { } disc)
            {
                bool dup = waiting.Any(w => w.StartsWith(disc.Root + "::", StringComparison.Ordinal));
                items.Add(dup ? new DropItem(p, DropKind.Duplicate, L.T("Already waiting in the queue."))
                              : new DropItem(p, DropKind.Disc, disc.Kind == DiscKind.BluRay ? "Blu-ray" : "DVD"));
                continue;
            }
            if (Directory.Exists(p))
            {
                var discs = DiscSource.FindDiscs(p);
                var exts = queue.Settings.FolderExtensions.Select(e => e.ToLowerInvariant()).ToHashSet();
                int videos = 0;
                try
                {
                    videos = Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Count(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !DiscSource.IsInsideDiscStructure(f)
                                    && !f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) && !QueueService.IsOwnOutput(f));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                items.Add(videos + discs.Count == 0
                    ? new DropItem(p, DropKind.Rejected, L.T("No video files or discs inside."))
                    : new DropItem(p, DropKind.Folder) { Videos = videos, Discs = discs.Count });
                continue;
            }
            if (Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(ReadPreset(p) is not null ? new DropItem(p, DropKind.Preset) : new DropItem(p, DropKind.Rejected, L.T("Not a KnA preset.")));
                continue;
            }
            if (!MediaExtensions.IsVideo(p))
            {
                items.Add(new DropItem(p, DropKind.Rejected, L.T("Unsupported file type ({0}).", Path.GetExtension(p))));
                continue;
            }
            if (waiting.Contains(Path.GetFullPath(p)))
            {
                items.Add(new DropItem(p, DropKind.Duplicate, L.T("Already waiting in the queue.")));
                continue;
            }
            items.Add(new DropItem(p, DropKind.Video));
            droppedVideos.Add(p);
        }

        foreach (var p in list.Where(IsExternal))
        {
            if (!File.Exists(p)) { items.Add(new DropItem(p, DropKind.Rejected, L.T("Not found."))); continue; }
            if (targetJob is { } tj && queue.Find(tj) is { } job)
            {
                items.Add(new DropItem(p, DropKind.External) { TargetJob = job.Id });
                continue;
            }
            // Same base name: a video dropped together, then a waiting job (longest name wins: "Ep01" vs "Ep0").
            var video = droppedVideos.Where(v => BelongsTo(p, v)).MaxBy(v => Path.GetFileNameWithoutExtension(v).Length);
            if (video is not null) { items.Add(new DropItem(p, DropKind.External) { TargetVideo = video }); continue; }
            var match = queue.Jobs.Where(j => j.Status is JobStatus.Ready or JobStatus.Stopped or JobStatus.Error && !DiscSource.IsTitlePath(j.SourcePath)
                                              && BelongsTo(p, j.SourcePath))
                .MaxBy(j => Path.GetFileNameWithoutExtension(j.SourcePath).Length);
            items.Add(match is not null
                ? new DropItem(p, DropKind.External) { TargetJob = match.Id }
                : new DropItem(p, DropKind.Rejected, L.T("No video in the queue with a matching name: drop it on the job it belongs to.")));
        }
        return new DropPlan(items);
    }

    /// <summary>"Ep01.ita.ass", "Ep01 [Commentary].flac", "Ep01.ttf"… belong to "Ep01.mkv" (same folder or not).</summary>
    public static bool BelongsTo(string external, string video)
    {
        var v = Path.GetFileNameWithoutExtension(video);
        var e = Path.GetFileNameWithoutExtension(external);
        if (v.Length == 0 || !e.StartsWith(v, StringComparison.OrdinalIgnoreCase)) return false;
        return e.Length == v.Length || e[v.Length] is '.' or ' ' or '_' or '-' or '[' or '(';
    }

    public static Preset? ReadPreset(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (text.Length > 1 << 20) return null;
            var preset = JsonSerializer.Deserialize<Preset>(text, SettingsStore.Json);
            if (preset is { Name.Length: > 0, Settings: not null }) return preset with { BuiltIn = false };
            var settings = JsonSerializer.Deserialize<JobSettings>(text, SettingsStore.Json);
            return settings is null ? null : new Preset(Path.GetFileNameWithoutExtension(path), null, settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { return null; }
    }

    /// <summary>Carries out a plan: queue, attach, import. Externals are probed and switched on in the job's track plan.</summary>
    public static async Task<DropResult> ApplyAsync(DropPlan plan, QueueService queue, JobSettings settings, CancellationToken ct = default)
    {
        var messages = new List<string>();
        int added = 0, attached = 0, presets = 0;
        var toQueue = plan.Items.Where(i => i.Kind is DropKind.Video or DropKind.Disc or DropKind.Folder).Select(i => i.Path).ToList();
        var jobs = toQueue.Count > 0 ? queue.AddPaths(toQueue, settings) : [];
        added = jobs.Count;

        foreach (var group in plan.Items.Where(i => i.Kind == DropKind.External).GroupBy(i => i.TargetJob?.ToString() ?? i.TargetVideo))
        {
            var first = group.First();
            var job = first.TargetJob is { } id ? queue.Find(id)
                : jobs.FirstOrDefault(j => j.SourcePath == Path.GetFullPath(first.TargetVideo!));
            if (job is null) { messages.Add(L.T("{0}: its video is not in the queue.", Path.GetFileName(first.Path))); continue; }
            try
            {
                await queue.AttachExternalsAsync(job.Id, group.Select(i => i.Path).ToList(), ct);
                attached += group.Count();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or KeyNotFoundException)
            {
                messages.Add($"{job.FileName}: {ex.Message}");
            }
        }

        foreach (var item in plan.Items.Where(i => i.Kind == DropKind.Preset))
            if (ReadPreset(item.Path) is { } p) { queue.Presets.Save(p); presets++; }

        foreach (var r in plan.Items.Where(i => i.Kind is DropKind.Rejected or DropKind.Duplicate))
            messages.Add($"{Path.GetFileName(r.Path)}: {r.Reason}");
        return new DropResult(added, attached, presets, messages);
    }
}
