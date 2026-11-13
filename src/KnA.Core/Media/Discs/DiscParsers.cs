using System.Buffers.Binary;
using System.Text;
using TextEncoding = System.Text.Encoding;

namespace KnA.Core.Media.Discs;

/// <summary>One play item of a Blu-ray playlist: a clip (BDMV/STREAM/xxxxx.m2ts) between two times.</summary>
public sealed record BdPlayItem(string Clip, double In, double Out);

public sealed record BdPlaylist(int Number, IReadOnlyList<BdPlayItem> Items, IReadOnlyList<double> Chapters,
    IReadOnlyDictionary<int, string> Languages)
{
    public TimeSpan Duration => TimeSpan.FromSeconds(Items.Sum(i => Math.Max(0, i.Out - i.In)));
    /// <summary>Same clips in the same order (duplicate playlists are common on discs).</summary>
    public string Signature => string.Join(",", Items.Select(i => $"{i.Clip}:{i.In:0.0}-{i.Out:0.0}"));
}

/// <summary>Blu-ray playlist (.mpls) and clip info (.clpi) parser. All times are seconds (45 kHz ticks / 45000).</summary>
public static class MplsParser
{
    public static BdPlaylist Parse(int number, byte[] d)
    {
        if (d.Length < 40 || TextEncoding.ASCII.GetString(d, 0, 4) != "MPLS") throw new InvalidDataException("Not an MPLS file.");
        int plStart = (int)U32(d, 8);
        int markStart = (int)U32(d, 12);
        int count = U16(d, plStart + 6);
        int pos = plStart + 10;
        var items = new List<BdPlayItem>();
        var langs = new Dictionary<int, string>();
        for (int i = 0; i < count; i++)
        {
            int len = U16(d, pos);
            int p = pos + 2;
            string clip = TextEncoding.ASCII.GetString(d, p, 5);
            bool multiAngle = (d[p + 10] & 0x10) != 0;
            double tin = U32(d, p + 12) / 45000.0, tout = U32(d, p + 16) / 45000.0;
            items.Add(new BdPlayItem(clip, tin, tout));
            if (i == 0)
            {
                int stn = p + 32;
                if (multiAngle) stn += 2 + (d[p + 32] - 1) * 10;
                ReadStnLanguages(d, stn, langs);
            }
            pos += 2 + len;
        }
        var chapters = new List<double>();
        if (markStart > 0 && markStart + 6 <= d.Length)
        {
            int marks = U16(d, markStart + 4);
            double offset = 0;
            var starts = new List<double>();
            foreach (var it in items) { starts.Add(offset); offset += it.Out - it.In; }
            for (int m = 0; m < marks; m++)
            {
                int q = markStart + 6 + m * 14;
                if (q + 14 > d.Length || d[q + 1] != 1) continue; // entry marks only
                int item = U16(d, q + 2);
                if (item >= items.Count) continue;
                double t = starts[item] + U32(d, q + 4) / 45000.0 - items[item].In;
                if (chapters.Count == 0 || t - chapters[^1] > 0.5) chapters.Add(Math.Max(0, t));
            }
        }
        return new BdPlaylist(number, items, chapters, langs);
    }

    /// <summary>PID → ISO 639-2 language from the STN table (the transport stream itself has no language tags).</summary>
    private static void ReadStnLanguages(byte[] d, int stn, Dictionary<int, string> langs)
    {
        try
        {
            int q = stn + 4;
            int total = d[q] + d[q + 1] + d[q + 2] + d[q + 3] + d[q + 4] + d[q + 5] + d[q + 6];
            q += 12;
            for (int s = 0; s < total && q + 2 < d.Length; s++)
            {
                int entryLen = d[q];
                int type = d[q + 1];
                int pid = type switch { 1 => U16(d, q + 2), 2 => U16(d, q + 4), 3 or 4 => U16(d, q + 3), _ => -1 };
                q += 1 + entryLen;
                int attrLen = d[q];
                int coding = d[q + 1];
                string? lang = coding switch
                {
                    >= 0x80 and <= 0x86 or 0xA1 or 0xA2 => TextEncoding.ASCII.GetString(d, q + 3, 3),
                    0x90 or 0x91 => TextEncoding.ASCII.GetString(d, q + 2, 3),
                    0x92 => TextEncoding.ASCII.GetString(d, q + 3, 3),
                    _ => null,
                };
                if (pid > 0 && lang is { Length: 3 } && lang.All(char.IsAsciiLetterLower)) langs.TryAdd(pid, lang);
                q += 1 + attrLen;
            }
        }
        catch (IndexOutOfRangeException) { /* best effort */ }
        catch (ArgumentOutOfRangeException) { }
    }

    /// <summary>Presentation start/end of a clip (CLPI SequenceInfo), or null.</summary>
    public static (double Start, double End)? ClipTimes(byte[] d)
    {
        try
        {
            if (TextEncoding.ASCII.GetString(d, 0, 4) != "HDMV") return null;
            int seq = (int)U32(d, 8);
            int q = seq + 4 + 1;
            int atc = d[q];
            if (atc < 1) return null;
            q += 1 + 4;
            int stcs = d[q];
            if (stcs < 1) return null;
            q += 2;
            double start = U32(d, q + 6) / 45000.0;
            double end = U32(d, q + 10) / 45000.0;
            // Several STC sequences: the last one ends the clip.
            int last = q + (stcs - 1) * 14;
            end = U32(d, last + 10) / 45000.0;
            return (start, end);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { return null; }
    }

    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));
    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));
}

/// <summary>DVD title as listed in VIDEO_TS.IFO (title search pointer table) with its duration from the VTS IFO.</summary>
public sealed record DvdTitle(int Number, int TitleSet, int TitleInSet, int Chapters, int Angles, TimeSpan Duration);

/// <summary>VIDEO_TS.IFO / VTS_xx_0.IFO parser.</summary>
public static class IfoParser
{
    public static List<DvdTitle> Titles(byte[] vmg, Func<int, byte[]?> readVtsIfo)
    {
        if (vmg.Length < 0x100 || TextEncoding.ASCII.GetString(vmg, 0, 12) != "DVDVIDEO-VMG") throw new InvalidDataException("Not a VIDEO_TS.IFO file.");
        int ttSrpt = (int)U32(vmg, 0xC4) * 2048;
        int n = U16(vmg, ttSrpt);
        var cache = new Dictionary<int, byte[]?>();
        var list = new List<DvdTitle>();
        for (int i = 0; i < n; i++)
        {
            int e = ttSrpt + 8 + i * 12;
            if (e + 12 > vmg.Length) break;
            int angles = vmg[e + 1], chapters = U16(vmg, e + 2), vts = vmg[e + 6], ttn = vmg[e + 7];
            if (!cache.TryGetValue(vts, out var vtsIfo)) cache[vts] = vtsIfo = readVtsIfo(vts);
            list.Add(new DvdTitle(i + 1, vts, ttn, chapters, angles, vtsIfo is null ? TimeSpan.Zero : Duration(vtsIfo, ttn)));
        }
        return list;
    }

    /// <summary>Playback time of the entry PGC of title <paramref name="ttn"/> inside a VTS.</summary>
    public static TimeSpan Duration(byte[] vts, int ttn)
    {
        try
        {
            if (TextEncoding.ASCII.GetString(vts, 0, 12) != "DVDVIDEO-VTS") return TimeSpan.Zero;
            int pgci = (int)U32(vts, 0xCC) * 2048;
            int count = U16(vts, pgci);
            TimeSpan best = TimeSpan.Zero;
            for (int i = 0; i < count; i++)
            {
                int e = pgci + 8 + i * 8;
                uint cat = U32(vts, e);
                int title = (int)((cat >> 24) & 0x7F);
                if (title != ttn) continue;
                int pgc = pgci + (int)U32(vts, e + 4);
                var t = Bcd(vts, pgc + 4);
                if (t > best) best = t;
            }
            return best;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { return TimeSpan.Zero; }
    }

    private static TimeSpan Bcd(byte[] d, int o)
    {
        static int B(byte b) => (b >> 4) * 10 + (b & 0x0F);
        double fps = (d[o + 3] >> 6) == 1 ? 25 : 29.97;
        int frames = B((byte)(d[o + 3] & 0x3F));
        return new TimeSpan(B(d[o]), B(d[o + 1]), B(d[o + 2])) + TimeSpan.FromSeconds(frames / fps);
    }

    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));
    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));
}
