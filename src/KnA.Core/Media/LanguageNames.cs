namespace KnA.Core.Media;

/// <summary>Language code equivalences: ISO 639-1 ("it"), 639-2/B ("ger") and 639-2/T ("deu") of the common languages.</summary>
public static class LanguageNames
{
    private static readonly string[][] Groups =
    [
        ["it", "ita"], ["en", "eng"], ["ja", "jpn"], ["fr", "fre", "fra"], ["de", "ger", "deu"], ["es", "spa"], ["pt", "por"],
        ["ru", "rus"], ["zh", "chi", "zho"], ["ko", "kor"], ["nl", "dut", "nld"], ["pl", "pol"], ["sv", "swe"], ["no", "nor", "nob", "nno"],
        ["da", "dan"], ["fi", "fin"], ["cs", "cze", "ces"], ["el", "gre", "ell"], ["tr", "tur"], ["ar", "ara"], ["he", "heb"],
        ["hu", "hun"], ["ro", "rum", "ron"], ["uk", "ukr"], ["hi", "hin"], ["th", "tha"], ["vi", "vie"], ["id", "ind"],
    ];

    /// <summary>True when both codes name the same language (case-insensitive).</summary>
    public static bool Same(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return Groups.Any(g => g.Contains(a.ToLowerInvariant()) && g.Contains(b.ToLowerInvariant()));
    }

    /// <summary>The two-letter code of a language code ("ita" → "it"), or the code itself when unknown.</summary>
    public static string TwoLetter(string code)
    {
        var c = code.ToLowerInvariant();
        return Groups.FirstOrDefault(g => g.Contains(c))?[0] ?? c;
    }

    /// <summary>Name of the language in the language itself, capitalised ("ita" → "Italiano"); null for none / "und".</summary>
    public static string? NativeName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("und", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var name = System.Globalization.CultureInfo.GetCultureInfo(TwoLetter(code.Trim())).NativeName;
            if (string.IsNullOrEmpty(name) || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) return code;
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) name = name[..paren];
            return char.ToUpper(name[0], System.Globalization.CultureInfo.InvariantCulture) + name[1..];
        }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }
}
