using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnA.Core.Processing;

namespace KnA.Core.Ocr;

/// <summary>
/// DVD subtitles (VobSub, "dvd_subtitle"): in .sub/.idx pairs, VOB/MPEG files and MKV tracks. FFprobe hands over the
/// packets (one sub-picture unit each) with their times and the .idx header (16-colour palette); KnA decodes the
/// interlaced 2-bit run-length pictures itself, the same way Subtitle Edit and DVD players do.
/// </summary>
public static partial class VobSubParser
{
    /// <summary>One packet of the track: presentation time, duration (0 = unknown) and data.</summary>
    public sealed record Packet(double Pts, double Duration, byte[] Data);

    /// <summary>Reads the packets and the header of one dvd_subtitle stream with FFprobe.</summary>
    public static async Task<(List<Packet> Packets, string? Header)> ReadAsync(string ffprobe, string file, int streamIndex, CancellationToken ct)
    {
        var args = new List<string> { "-v", "error", "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
            "-show_packets", "-show_data", "-show_streams", "-show_entries", "packet=pts_time,duration_time,data:stream=extradata", "-of", "json" };
        args.AddRange(Media.Discs.DiscSource.ProbeArgs(file));
        var r = await ProcessRunner.RunAsync(ffprobe, args, timeout: TimeSpan.FromMinutes(20), ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("ffprobe: " + r.StdErr.Trim());
        using var doc = JsonDocument.Parse(r.StdOut);
        var packets = new List<Packet>();
        if (doc.RootElement.TryGetProperty("packets", out var list))
            foreach (var p in list.EnumerateArray())
            {
                if (!p.TryGetProperty("data", out var d) || !double.TryParse(Str(p, "pts_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)) continue;
                double.TryParse(Str(p, "duration_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur);
                packets.Add(new Packet(pts, dur, HexDump(d.GetString() ?? "")));
            }
        string? header = null;
        if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0 && streams[0].TryGetProperty("extradata", out var ex))
            header = System.Text.Encoding.ASCII.GetString(HexDump(ex.GetString() ?? ""));
        return (packets, header);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : null;

    /// <summary>FFprobe's hexdump ("00000000: 0011 2233 ...  ascii") → bytes.</summary>
    public static byte[] HexDump(string text)
    {
        var bytes = new List<byte>();
        foreach (var line in text.Split('\n'))
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) continue;
            // hex groups end two spaces before the ASCII column
            var hex = line[(colon + 2)..];
            int ascii = hex.IndexOf("  ", StringComparison.Ordinal);
            if (ascii >= 0) hex = hex[..ascii];
            foreach (var group in hex.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                for (int i = 0; i + 1 < group.Length; i += 2)
                    bytes.Add(byte.Parse(group.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return [.. bytes];
    }

    /// <summary>The 16 colours of the .idx header ("palette: 000000, ffffff, …"); a grey ramp when missing.</summary>
    public static uint[] Palette(string? header)
    {
        var pal = new uint[16];
        var m = header is null ? null : PaletteLine().Match(header);
        var colors = m is { Success: true } ? m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
        for (int i = 0; i < 16; i++)
            pal[i] = i < colors.Length && uint.TryParse(colors[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
                ? rgb : (uint)(i * 17 * 0x010101);
        return pal;
    }

    [GeneratedRegex(@"palette:\s*([0-9a-fA-F,\s]+)")] private static partial Regex PaletteLine();

    public static List<SubtitleImage> Parse(IReadOnlyList<Packet> packets, string? header)
    {
        var palette = Palette(header);
        var result = new List<SubtitleImage>();
        for (int k = 0; k < packets.Count; k++)
        {
            var p = packets[k];
            if (Decode(p.Data, palette) is not { } spu || IsEmpty(spu.Rgba)) continue;   // empty units only clear the screen
            double start = p.Pts + spu.StartDelay;
            double end = spu.StopDelay is { } stop ? p.Pts + stop
                : p.Duration is > 0 and < 60 ? p.Pts + p.Duration
                : k + 1 < packets.Count ? packets[k + 1].Pts : start + 4;
            if (end <= start) end = start + 0.5;
            result.Add(new SubtitleImage(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), spu.Width, spu.Height, spu.Rgba) { Forced = spu.Forced });
        }
        return result;
    }

    private static bool IsEmpty(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] > 16) return false;
        return true;
    }

    private sealed record Spu(int Width, int Height, byte[] Rgba, double StartDelay, double? StopDelay, bool Forced);

    private static Spu? Decode(byte[] d, uint[] palette)
    {
        if (d.Length < 4) return null;
        int size = (d[0] << 8) | d[1], ctrl = (d[2] << 8) | d[3];
        if (size > d.Length || ctrl >= d.Length) return null;
        int[] colorIdx = [0, 1, 2, 3], alpha = [0, 15, 15, 15];
        int x1 = 0, x2 = -1, y1 = 0, y2 = -1, top = -1, bottom = -1;
        double startDelay = 0; double? stopDelay = null;
        bool forced = false;
        int seq = ctrl, guard = 0;
        while (seq + 4 <= d.Length && guard++ < 64)
        {
            double delay = ((d[seq] << 8) | d[seq + 1]) * 1024.0 / 90000.0;
            int next = (d[seq + 2] << 8) | d[seq + 3];
            int i = seq + 4;
            while (i < d.Length && d[i] != 0xFF)
            {
                switch (d[i++])
                {
                    case 0x00: forced = true; startDelay = delay; break;
                    case 0x01: startDelay = delay; break;
                    // 0xFFFF ("until further notice", written by some encoders) is not a real end time
                    case 0x02: stopDelay = ((d[seq] << 8) | d[seq + 1]) == 0xFFFF || delay > 60 ? null : delay; break;
                    case 0x03 when i + 2 <= d.Length:
                        colorIdx = [d[i + 1] & 0xF, d[i + 1] >> 4, d[i] & 0xF, d[i] >> 4]; i += 2; break;
                    case 0x04 when i + 2 <= d.Length:
                        alpha = [d[i + 1] & 0xF, d[i + 1] >> 4, d[i] & 0xF, d[i] >> 4]; i += 2; break;
                    case 0x05 when i + 6 <= d.Length:
                        x1 = (d[i] << 4) | (d[i + 1] >> 4); x2 = ((d[i + 1] & 0xF) << 8) | d[i + 2];
                        y1 = (d[i + 3] << 4) | (d[i + 4] >> 4); y2 = ((d[i + 4] & 0xF) << 8) | d[i + 5]; i += 6; break;
                    case 0x06 when i + 4 <= d.Length:
                        top = (d[i] << 8) | d[i + 1]; bottom = (d[i + 2] << 8) | d[i + 3]; i += 4; break;
                    default: i = d.Length; break;   // unknown command: stop reading this sequence
                }
            }
            if (next == seq || next >= d.Length) break;
            seq = next;
        }
        int w = x2 - x1 + 1, h = y2 - y1 + 1;
        if (w <= 0 || h <= 0 || top < 0 || bottom < 0 || w > 4096 || h > 4096) return null;
        var rgba = new byte[w * h * 4];
        var colors = new uint[4];
        for (int c = 0; c < 4; c++)
            colors[c] = (palette[colorIdx[c]] << 8) | (uint)(alpha[c] * 17);
        DecodeField(d, top, ctrl, w, h, 0, colors, rgba);
        DecodeField(d, bottom, ctrl, w, h, 1, colors, rgba);
        return new Spu(w, h, rgba, startDelay, stopDelay, forced);
    }

    /// <summary>One field (even or odd lines): 2-bit colour runs coded in 4/8/12/16-bit groups of nibbles.</summary>
    private static void DecodeField(byte[] d, int offset, int limit, int w, int h, int first, uint[] colors, byte[] rgba)
    {
        int nib = offset * 2;   // position in nibbles
        int Nibble() { int b = nib >> 1; if (b >= limit || b >= d.Length) return 0; int v = (nib & 1) == 0 ? d[b] >> 4 : d[b] & 0xF; nib++; return v; }
        for (int y = first; y < h; y += 2)
        {
            int x = 0;
            while (x < w)
            {
                if ((nib >> 1) >= limit) return;
                int v = Nibble();
                if (v < 0x4) { v = (v << 4) | Nibble(); if (v < 0x10) { v = (v << 4) | Nibble(); if (v < 0x40) v = (v << 4) | Nibble(); } }
                int run = v >> 2, c = v & 3;
                if (run == 0) run = w - x;   // to the end of the line
                uint col = colors[c];
                for (int k = 0; k < run && x < w; k++, x++)
                {
                    int at = (y * w + x) * 4;
                    rgba[at] = (byte)(col >> 24); rgba[at + 1] = (byte)(col >> 16); rgba[at + 2] = (byte)(col >> 8); rgba[at + 3] = (byte)col;
                }
            }
            if ((nib & 1) != 0) nib++;   // lines start on a byte boundary
        }
    }
}
