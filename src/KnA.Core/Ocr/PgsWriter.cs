namespace KnA.Core.Ocr;

/// <summary>Writes Blu-ray PGS (.sup) from indexed pictures (FFmpeg can read PGS but not create it): used by the tests.</summary>
public static class PgsWriter
{
    public sealed record Picture(double Start, double End, int Width, int Height, byte[] Indexed, int X = 100, int Y = 900);

    public static byte[] Write(IEnumerable<Picture> pictures)
    {
        var o = new List<byte>();
        foreach (var p in pictures)
        {
            long s = (long)(p.Start * 90000), e = (long)(p.End * 90000);
            // PCS epoch start with one object
            Seg(o, s, 0x16, [0x07, 0x80, 0x04, 0x38, 0x10, 0, 1, 0x80, 0, 0, 1, 0, 0, 0, 0, (byte)(p.X >> 8), (byte)p.X, (byte)(p.Y >> 8), (byte)p.Y]);
            Seg(o, s, 0x17, [1, 0, (byte)(p.X >> 8), (byte)p.X, (byte)(p.Y >> 8), (byte)p.Y, (byte)(p.Width >> 8), (byte)p.Width, (byte)(p.Height >> 8), (byte)p.Height]);
            // palette: 1 = white, 2 = black outline, 3 = grey (antialiasing); 0 stays transparent
            Seg(o, s, 0x14, [0, 0, 1, 235, 128, 128, 255, 2, 16, 128, 128, 255, 3, 126, 128, 128, 255]);
            var rle = Rle(p.Width, p.Height, p.Indexed);
            int len = rle.Count + 4;
            Seg(o, s, 0x15, [0, 0, 0, 0xC0, (byte)(len >> 16), (byte)(len >> 8), (byte)len, (byte)(p.Width >> 8), (byte)p.Width, (byte)(p.Height >> 8), (byte)p.Height, .. rle]);
            Seg(o, s, 0x80, []);
            // clear at the end
            Seg(o, e, 0x16, [0x07, 0x80, 0x04, 0x38, 0x10, 0, 2, 0x00, 0, 0, 0]);
            Seg(o, e, 0x80, []);
        }
        return [.. o];
    }

    private static void Seg(List<byte> o, long pts, byte type, byte[] payload)
    {
        o.AddRange([(byte)'P', (byte)'G', (byte)(pts >> 24), (byte)(pts >> 16), (byte)(pts >> 8), (byte)pts, 0, 0, 0, 0, type,
            (byte)(payload.Length >> 8), (byte)payload.Length]);
        o.AddRange(payload);
    }

    private static List<byte> Rle(int w, int h, byte[] px)
    {
        var r = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                byte c = px[y * w + x];
                int n = 1;
                while (x + n < w && px[y * w + x + n] == c && n < 16383) n++;
                if (c != 0 && n < 3) for (int k = 0; k < n; k++) r.Add(c);
                else if (c == 0) { r.Add(0); if (n < 64) r.Add((byte)n); else { r.Add((byte)(0x40 | (n >> 8))); r.Add((byte)n); } }
                else { r.Add(0); if (n < 64) { r.Add((byte)(0x80 | n)); } else { r.Add((byte)(0xC0 | (n >> 8))); r.Add((byte)n); } r.Add(c); }
                x += n;
            }
            r.Add(0); r.Add(0);
        }
        return r;
    }
}

