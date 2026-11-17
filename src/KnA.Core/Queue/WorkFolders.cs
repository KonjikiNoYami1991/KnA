using KnA.Core.Settings;

namespace KnA.Core.Queue;

/// <summary>Where a job keeps its working files (temporary files, parallel segments, resumable pieces).</summary>
public enum WorkLocation
{
    /// <summary>A hidden ".kna-work" folder next to the source video: same disk, no copy through the system drive.</summary>
    NextToSource,
    /// <summary>KnA's data folder (or the temporary folder set in the settings).</summary>
    DataFolder,
}

/// <summary>
/// The working folders of a job. Next to the source by default: the big intermediate files (lossless segments,
/// resumable pieces, extracted audio) are written on the same disk as the video and the result, not on the
/// system drive, and a resumable encode survives a reinstall. Read-only folders, discs and ISO images fall back
/// to the data folder by themselves.
/// </summary>
public static class WorkFolders
{
    public const string FolderName = ".kna-work";

    /// <summary>Set from the settings by <see cref="QueueService"/>.</summary>
    public static WorkLocation Location { get; set; } = WorkLocation.NextToSource;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Writable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>".kna-work" next to the source, or null when it can't be used (setting, disc, read-only folder).</summary>
    public static string? NextToSource(string source)
    {
        if (Location != WorkLocation.NextToSource || Media.Discs.DiscSource.IsTitlePath(source)) return null;
        string? dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(source)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var root = Path.Combine(dir, FolderName);
        return Writable.GetOrAdd(root, Probe) ? root : null;
    }

    private static bool Probe(string root)
    {
        try
        {
            bool existed = Directory.Exists(root);
            var info = Directory.CreateDirectory(root);
            if (!existed && OperatingSystem.IsWindows()) info.Attributes |= FileAttributes.Hidden;
            var probe = Path.Combine(root, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            if (!existed) TryRemoveEmpty(root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>Temporary files of one run of a job.</summary>
    public static string Temp(QueueJob job, SettingsStore store) =>
        NextToSource(job.SourcePath) is { } root ? Path.Combine(root, "tmp-" + job.Id.ToString("N")[..12]) : Path.Combine(store.TempDirectory, job.Id.ToString("N"));

    /// <summary>Parent of a job's segment / piece folders (kept between runs for resuming).</summary>
    public static string Chunks(QueueJob job, SettingsStore store) =>
        NextToSource(job.SourcePath) ?? Path.Combine(store.DataDirectory, "chunks");

    /// <summary>A job left the queue: its pieces and segments (kept for resuming) go, and ".kna-work" when empty.</summary>
    public static void Forget(QueueJob job, SettingsStore store)
    {
        try
        {
            var key = ChunkedEncoder.Key(job)[..16];
            var root = Chunks(job, store);
            foreach (var d in new[] { key, "r-" + key, "x-" + key, "tmp-" + job.Id.ToString("N")[..12] })
                try { if (Directory.Exists(Path.Combine(root, d))) Directory.Delete(Path.Combine(root, d), true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NullReferenceException) { }
        TidyUp(job);
    }

    /// <summary>Deletes ".kna-work" next to the source when nothing is left in it.</summary>
    public static void TidyUp(QueueJob job)
    {
        if (Location != WorkLocation.NextToSource) return;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(job.SourcePath));
            if (dir is not null) TryRemoveEmpty(Path.Combine(dir, FolderName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    private static void TryRemoveEmpty(string root)
    {
        try { if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
