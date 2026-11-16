namespace KnA.Core.Ocr;

/// <summary>One subtitle picture: when it shows and its pixels (RGBA, row by row).</summary>
public sealed record SubtitleImage(TimeSpan Start, TimeSpan End, int Width, int Height, byte[] Rgba)
{
    /// <summary>Optional: the picture was forced on (PGS "forced" flag of the composition object).</summary>
    public bool Forced { get; init; }
}

/// <summary>
/// Reads Blu-ray PGS subtitles (.sup, "hdmv_pgs_subtitle"): presentation/window/palette/object segments, the
/// run-length coded bitmaps decoded with their YCbCr palette into RGBA pictures with start and end times.
/// The same decoding Subtitle Edit and every player do; no external program involved.
/// </summary>
public static class PgsParser
{
    private const byte Pds = 0x14, Ods = 0x15, Pcs = 0x16, Wds = 0x17, End = 0x80;

    private sealed class ObjectData
    {
        public int Width, Height;
        public List<byte> Rle = [];
    }

    private sealed record CompositionObject(int ObjectId, int X, int Y, bool Forced, (int X, int Y, int W, int H)? Crop);

    public static List<SubtitleImage> Parse(string supFile) => Parse(File.ReadAllBytes(supFile));

    public static List<SubtitleImage> Parse(byte[] data)
    {
        var result = new List<SubtitleImage>();
        var palettes = new Dictionary<int, uint[]>();
        var objects = new Dictionary<int, ObjectData>();
        List<CompositionObject> shown = [];
        int paletteId = 0;
        long shownPts = -1;
        SubtitleImage? pending = null;
        int pos = 0;

        while (pos + 13 <= data.Length)
        {
            if (data[pos] != 'P' || data[pos + 1] != 'G') { pos++; continue; }   // resync on garbage
            long pts = ReadU32(data, pos + 2);
            byte type = data[pos + 10];
            int size = (data[pos + 11] << 8) | data[pos + 12];
            int seg = pos + 13;
            if (seg + size > data.Length) break;
            switch (type)
            {
                case Pcs:
                {
                    // composition: what is on screen from this time on (no objects = clear the screen)
                    int state = data[seg + 7] >> 6;           // 0 normal, 1 acquisition point, 2 epoch start
                    paletteId = data[seg + 9];
                    int count = data[seg + 10];
                    var list = new List<CompositionObject>();
                    int p = seg + 11;
                    for (int i = 0; i < count && p + 8 <= seg + size; i++)
                    {
                        int objId = (data[p] << 8) | data[p + 1];
                        byte flags = data[p + 3];
                        int x = (data[p + 4] << 8) | data[p + 5], y = (data[p + 6] << 8) | data[p + 7];
                        p += 8;
                        (int, int, int, int)? crop = null;
                        if ((flags & 0x80) != 0 && p + 8 <= seg + size)
                        {
                            crop = ((data[p] << 8) | data[p + 1], (data[p + 2] << 8) | data[p + 3], (data[p + 4] << 8) | data[p + 5], (data[p + 6] << 8) | data[p + 7]);
                            p += 8;
                        }
                        list.Add(new CompositionObject(objId, x, y, (flags & 0x40) != 0, crop));
                    }
                    if (state == 2) objects.Clear();   // epoch start: object buffer reset
                    // the picture shown until now ends here
                    if (pending is not null)
                    {
                        result.Add(pending with { End = Ts(pts) });
                        pending = null;
                    }
                    shown = list;
                    shownPts = pts;
                    break;
                }
                case Pds:
                {
                    int id = data[seg];
                    var pal = palettes.TryGetValue(id, out var existing) ? existing : new uint[256];
                    for (int p = seg + 2; p + 5 <= seg + size; p += 5)
                    {
                        int idx = data[p];
                        pal[idx] = YCbCrToRgba(data[p + 1], data[p + 3], data[p + 2], data[p + 4]);
                    }
                    palettes[id] = pal;
                    break;
                }
                case Ods:
                {
                    int id = (data[seg] << 8) | data[seg + 1];
                    byte seq = data[seg + 3];
                    int p = seg + 4;
                    if ((seq & 0x80) != 0)   // first fragment: data length (3 bytes), width, height
                    {
                        var o = new ObjectData { Width = (data[p + 3] << 8) | data[p + 4], Height = (data[p + 5] << 8) | data[p + 6] };
                        objects[id] = o;
                        p += 7;
                    }
                    if (objects.TryGetValue(id, out var obj))
                        for (; p < seg + size; p++) obj.Rle.Add(data[p]);
                    break;
                }
                case End:
                {
                    // the display set is complete: render what the composition shows
                    if (shown.Count > 0 && palettes.TryGetValue(paletteId, out var pal) && Render(shown, objects, pal) is { } img)
                        pending = img with { Start = Ts(shownPts), End = Ts(shownPts) };
                    break;
                }
                case Wds: break;
            }
            pos = seg + size;
        }
        if (pending is not null) result.Add(pending with { End = pending.Start + TimeSpan.FromSeconds(4) });
        // Acquisition points repeat the picture on screen (for seeking): one subtitle, not several.
        var merged = new List<SubtitleImage>();
        foreach (var r in result.Where(r => r.Width > 0 && r.Height > 0))
        {
            if (merged.Count > 0 && merged[^1] is var last && (r.Start - last.End).TotalMilliseconds < 50
                && last.Width == r.Width && last.Height == r.Height && last.Rgba.AsSpan().SequenceEqual(r.Rgba))
                merged[^1] = last with { End = r.End };
            else merged.Add(r);
        }
        return merged;
    }

    private static SubtitleImage? Render(List<CompositionObject> shown, Dictionary<int, ObjectData> objects, uint[] palette)
    {
        var parts = new List<(int X, int Y, int W, int H, byte[] Px)>();
        bool forced = false;
        foreach (var c in shown)
        {
            if (!objects.TryGetValue(c.ObjectId, out var o) || o.Width == 0 || o.Height == 0) continue;
            var px = DecodeRle(o, palette);
            int x = c.X, y = c.Y, w = o.Width, h = o.Height;
            if (c.Crop is { } cr)
            {
                // cropped object: keep only the cropped rectangle
                var cut = new byte[cr.W * cr.H * 4];
                for (int row = 0; row < cr.H && cr.Y + row < h; row++)
                    Array.Copy(px, ((cr.Y + row) * w + cr.X) * 4, cut, row * cr.W * 4, Math.Max(0, Math.Min(cr.W, w - cr.X)) * 4);
                px = cut; w = cr.W; h = cr.H;
            }
            parts.Add((x, y, w, h, px));
            forced |= c.Forced;
        }
        if (parts.Count == 0) return null;
        int left = parts.Min(p => p.X), top = parts.Min(p => p.Y);
        int width = parts.Max(p => p.X + p.W) - left, height = parts.Max(p => p.Y + p.H) - top;
        var rgba = new byte[width * height * 4];
        foreach (var (x, y, w, h, px) in parts)
            for (int row = 0; row < h; row++)
                Array.Copy(px, row * w * 4, rgba, ((y - top + row) * width + (x - left)) * 4, w * 4);
        return new SubtitleImage(TimeSpan.Zero, TimeSpan.Zero, width, height, rgba) { Forced = forced };
    }

    /// <summary>PGS run-length coding: 0x00 escapes runs and line ends; anything else is one pixel.</summary>
    private static byte[] DecodeRle(ObjectData o, uint[] palette)
    {
        var px = new byte[o.Width * o.Height * 4];
        var rle = o.Rle;
        int x = 0, y = 0, i = 0;
        void Put(int color, int count)
        {
            uint c = palette[color];
            for (int k = 0; k < count && x < o.Width; k++, x++)
            {
                int at = (y * o.Width + x) * 4;
                if (at + 3 >= px.Length) return;
                px[at] = (byte)(c >> 24); px[at + 1] = (byte)(c >> 16); px[at + 2] = (byte)(c >> 8); px[at + 3] = (byte)c;
            }
        }
        while (i < rle.Count && y < o.Height)
        {
            byte b = rle[i++];
            if (b != 0) { Put(b, 1); continue; }
            if (i >= rle.Count) break;
            byte f = rle[i++];
            if (f == 0) { x = 0; y++; continue; }                          // end of line
            int len = f & 0x3F;
            if ((f & 0x40) != 0 && i < rle.Count) len = (len << 8) | rle[i++];
            int color = 0;
            if ((f & 0x80) != 0 && i < rle.Count) color = rle[i++];
            Put(color, len);
        }
        return px;
    }

    /// <summary>BT.709 limited-range YCbCr (as Blu-ray palettes are) → RGBA packed as 0xRRGGBBAA.</summary>
    private static uint YCbCrToRgba(byte y, byte cb, byte cr, byte a)
    {
        double yy = 1.164 * (y - 16), u = cb - 128, v = cr - 128;
        static byte C(double d) => (byte)Math.Clamp((int)Math.Round(d), 0, 255);
        byte r = C(yy + 1.793 * v), g = C(yy - 0.213 * u - 0.533 * v), b = C(yy + 2.112 * u);
        return ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
    }

    private static long ReadU32(byte[] d, int p) => ((long)d[p] << 24) | ((long)d[p + 1] << 16) | ((long)d[p + 2] << 8) | d[p + 3];

    private static TimeSpan Ts(long pts90k) => TimeSpan.FromTicks(pts90k * TimeSpan.TicksPerMillisecond / 90);
}
