using System.Text;
using TextEncoding = System.Text.Encoding;

namespace KnA.Core.Media.Discs;

/// <summary>
/// Minimal read-only ISO 9660 reader: enough to find VIDEO_TS/*.IFO inside a DVD image
/// (DVD-Video discs use the UDF/ISO 9660 bridge format, so the ISO 9660 tree is always there).
/// Blu-ray images are UDF 2.5 only: <see cref="Open"/> returns null for them.
/// </summary>
public sealed class Iso9660 : IDisposable
{
    private const int Sector = 2048;
    private readonly FileStream _fs;
    private readonly (long Lba, int Size) _root;

    public sealed record Entry(string Name, long Lba, long Size, bool IsDirectory);

    private Iso9660(FileStream fs, (long, int) root)
    {
        _fs = fs;
        _root = root;
    }

    /// <summary>Opens the image; null when it has no ISO 9660 primary volume descriptor.</summary>
    public static Iso9660? Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var buf = new byte[Sector];
            for (int s = 16; s < 32; s++)
            {
                fs.Position = (long)s * Sector;
                if (fs.Read(buf, 0, Sector) < Sector) break;
                if (TextEncoding.ASCII.GetString(buf, 1, 5) != "CD001") break;
                if (buf[0] == 255) break; // terminator
                if (buf[0] != 1) continue; // not the primary descriptor
                long lba = BitConverter.ToUInt32(buf, 156 + 2);
                int size = (int)BitConverter.ToUInt32(buf, 156 + 10);
                return new Iso9660(fs, (lba, size));
            }
        }
        catch (IOException) { }
        fs.Dispose();
        return null;
    }

    public IReadOnlyList<Entry> List(string directory)
    {
        var (lba, size) = _root;
        foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = ReadDir(lba, size).FirstOrDefault(x => x.IsDirectory && x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (e is null) return [];
            (lba, size) = (e.Lba, (int)e.Size);
        }
        return ReadDir(lba, size);
    }

    public byte[] ReadFile(Entry e, int maxBytes = 16 << 20)
    {
        int n = (int)Math.Min(e.Size, maxBytes);
        var data = new byte[n];
        _fs.Position = e.Lba * Sector;
        _fs.ReadExactly(data, 0, n);
        return data;
    }

    private List<Entry> ReadDir(long lba, int size)
    {
        var list = new List<Entry>();
        var data = new byte[Math.Max(size, Sector)];
        _fs.Position = lba * Sector;
        int read = _fs.Read(data, 0, size);
        int pos = 0;
        while (pos < read)
        {
            int len = data[pos];
            if (len == 0)
            {
                // Records never cross sector boundaries: jump to the next sector.
                pos = (pos / Sector + 1) * Sector;
                continue;
            }
            if (pos + 33 > read) break;
            long extent = BitConverter.ToUInt32(data, pos + 2);
            long dataLen = BitConverter.ToUInt32(data, pos + 10);
            bool dir = (data[pos + 25] & 2) != 0;
            int nameLen = data[pos + 32];
            if (pos + 33 + nameLen > read) break;
            string name = TextEncoding.ASCII.GetString(data, pos + 33, nameLen);
            if (nameLen == 1 && data[pos + 33] is 0 or 1) name = ""; // "." and ".."
            int semi = name.IndexOf(';');
            if (semi >= 0) name = name[..semi];
            if (name.Length > 0) list.Add(new Entry(name.TrimEnd('.'), extent, dataLen, dir));
            pos += len;
        }
        return list;
    }

    public void Dispose() => _fs.Dispose();
}
