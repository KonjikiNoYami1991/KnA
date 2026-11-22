namespace KnA.Core.Encoding;

/// <summary>One filter of an FFmpeg chain: its name and its options (key empty = positional value).</summary>
public sealed record FilterCall(string Name, List<KeyValuePair<string, string>> Options);

/// <summary>"scale_cuda=w=1280:h=720,bwdif_cuda=mode=1" ↔ filters with their options (the editors' GPU filter lists).</summary>
public static class FilterChain
{
    public static List<FilterCall> Parse(string? chain)
    {
        var list = new List<FilterCall>();
        foreach (var part in Split(chain ?? "", ','))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            int eq = p.IndexOf('=');
            var name = eq < 0 ? p : p[..eq];
            var opts = new List<KeyValuePair<string, string>>();
            if (eq >= 0)
                foreach (var o in Split(p[(eq + 1)..], ':'))
                {
                    int e = o.IndexOf('=');
                    opts.Add(e < 0 ? new("", o) : new(o[..e], o[(e + 1)..]));
                }
            list.Add(new FilterCall(name.Trim(), opts));
        }
        return list;
    }

    public static string? Format(IEnumerable<FilterCall> filters)
    {
        var text = string.Join(',', filters.Where(f => f.Name.Length > 0).Select(f =>
        {
            var opts = f.Options.Where(o => o.Value.Length > 0).Select(o => o.Key.Length == 0 ? o.Value : o.Key + "=" + o.Value).ToList();
            return opts.Count == 0 ? f.Name : f.Name + "=" + string.Join(':', opts);
        }));
        return text.Length == 0 ? null : text;
    }

    /// <summary>Splits on <paramref name="sep"/> outside quotes, brackets and backslash escapes.</summary>
    private static IEnumerable<string> Split(string text, char sep)
    {
        var cur = new System.Text.StringBuilder();
        int depth = 0;
        bool quote = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length) { cur.Append(c).Append(text[++i]); continue; }
            if (c == '\'') quote = !quote;
            else if (!quote && c == '[') depth++;
            else if (!quote && c == ']') depth = Math.Max(0, depth - 1);
            else if (!quote && depth == 0 && c == sep) { yield return cur.ToString(); cur.Clear(); continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }
}
