using System.IO.Compression;

namespace KnA.Core.Ocr;

/// <summary>Minimal PNG encoder (8-bit grayscale), enough to hand pictures to the OCR engine.</summary>
public static class PngWriter
{
    public static void WriteGray(string path, int width, int height, byte[] gray)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)width);
        BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 0;   // grayscale
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);   // filter: none
                z.Write(gray, y * width, width);
            }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    /// <summary>RGBA picture (for showing subtitle pictures in the web OCR editor).</summary>
    public static byte[] Rgba(int width, int height, byte[] rgba)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)width);
        BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;   // RGBA
        Chunk(ms, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgba, y * width * 4, width * 4);
            }
        Chunk(ms, "IDAT", raw.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc(t, 0xFFFFFFFF);
        crc = Crc(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4];
        BigEndian(c, 0, crc);
        s.Write(c);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(byte[] data, uint crc)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void BigEndian(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }
}
