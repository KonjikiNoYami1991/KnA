using KnA.Core.Encoding;
using KnA.Core.Localization;
using KnA.Core.Media;

namespace KnA.Core.Accounts;

/// <summary>
/// The parts of job settings that run hand-written code or arguments on the server: VapourSynth / AviSynth scripts
/// written by hand, extra encoder and tool arguments, custom FFmpeg filters, a model loaded from a file. A user whose
/// account lacks <see cref="AccountPermissions.Advanced"/> may not bring new ones in, but may keep and use those that
/// are already there (a preset or the defaults an administrator made, a job's current settings).
/// Catalogue filter chains, rigaya's --vpp-* GPU filters and every other setting are always allowed.
/// </summary>
public static class AdvancedContent
{
    public sealed record Item(string What, string Value);

    public static IEnumerable<Item> Of(JobSettings? s, JobEdit? edit = null)
    {
        if (s is not null)
        {
            var fs = s.Frameserver;
            if (fs.Kind != FrameserverKind.None && fs.Chain is null && !string.IsNullOrWhiteSpace(fs.Script))
                yield return new(L.T("a script written by hand"), fs.Script.Trim());
            if (Has(s.Encoder.Extra)) yield return new(L.T("extra encoder options"), s.Encoder.Extra!.Trim());
            if (Has(s.Encoder.GpuFilters) && !OnlyGpuFilters(s.Encoder.GpuFilters!)) yield return new(L.T("encoder options among the GPU filters"), s.Encoder.GpuFilters!.Trim());
            if (Has(s.VideoFilters.Custom)) yield return new(L.T("custom video filters"), s.VideoFilters.Custom!.Trim());
            if (Has(s.VideoFilters.Gpu)) yield return new(L.T("custom GPU filters"), s.VideoFilters.Gpu!.Trim());
            if (Has(s.AudioFilters.Custom)) yield return new(L.T("custom audio filters"), s.AudioFilters.Custom!.Trim());
            foreach (var a in new[] { s.Arguments.AudioFfmpeg, s.Arguments.Fdkaac, s.Arguments.Qaac, s.Arguments.Subtitles, s.Arguments.Muxer })
                if (Has(a)) yield return new(L.T("hand-written program options"), a!.Trim());
            if (s.Upscale.Model is { } m && (m.Contains('/') || m.Contains('\\')))
                yield return new(L.T("a model file"), m.Trim());
        }
        foreach (var t in edit?.Tracks ?? [])
        {
            if (Has(t.Arguments)) yield return new(L.T("hand-written program options"), t.Arguments!.Trim());
            if (Has(t.Filters?.Custom)) yield return new(L.T("custom audio filters"), t.Filters!.Custom!.Trim());
        }
    }

    /// <summary>
    /// The first advanced part of <paramref name="settings"/> / <paramref name="edit"/> that is not among
    /// <paramref name="allowed"/>; null when there is none (the request may go on).
    /// </summary>
    public static Item? FirstNew(JobSettings? settings, JobEdit? edit, IEnumerable<Item> allowed)
    {
        var known = allowed.Select(i => i.Value).ToHashSet(StringComparer.Ordinal);
        return Of(settings, edit).FirstOrDefault(i => !known.Contains(i.Value));
    }

    /// <summary>Message for a user who may not use <paramref name="item"/>.</summary>
    public static string Denied(Item item) =>
        L.T("Your account may not use {0}: ask an administrator to allow advanced mode (Settings → Accounts).", item.What);

    /// <summary>A video source that is itself a script (.vpy / .avs): code that runs on the server.</summary>
    public static bool IsScriptSource(string path) => Frameserver.KindOf(path) != FrameserverKind.None;

    /// <summary>rigaya options that are only GPU filters (--vpp-*), with their values.</summary>
    internal static bool OnlyGpuFilters(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(tok => tok.StartsWith('-') && !double.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .All(tok => tok.StartsWith("--vpp-", StringComparison.Ordinal));

    private static bool Has(string? v) => !string.IsNullOrWhiteSpace(v);
}
