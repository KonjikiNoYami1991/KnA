using System.Globalization;

namespace TnA.Core.Localization;

/// <summary>
/// Minimal gettext-style localization: the English text is the key, Italian is a lookup table.
/// The language follows <see cref="CultureInfo.CurrentUICulture"/>: Italian systems get Italian,
/// everything else English. Set TNA_LANG=it|en to force one.
/// </summary>
public static partial class L
{
    private static readonly string? Forced = Environment.GetEnvironmentVariable("TNA_LANG")?.Trim().ToLowerInvariant();

    public static bool IsItalian(CultureInfo? culture = null) =>
        Forced is "it" || (Forced is not "en" && (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "it");

    public static string Language => IsItalian() ? "it" : "en";

    /// <summary>Translates an English text (and formats it when arguments are given).</summary>
    public static string T(string english, params object?[] args)
    {
        var text = IsItalian() && It.TryGetValue(english, out var it) ? it : english;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }

    /// <summary>All Italian translations (for clients that translate on their side).</summary>
    public static IReadOnlyDictionary<string, string> ItalianTable => It;
}
