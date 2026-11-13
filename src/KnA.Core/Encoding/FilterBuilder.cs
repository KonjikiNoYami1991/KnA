using System.Globalization;
using KnA.Core.Media;

namespace KnA.Core.Encoding;

/// <summary>
/// Video chain order:
///   [rotation from the display matrix, done by FFmpeg before the graph] → deinterlace → crop
///   → HDR tone map → denoise → deband → [bitmap subtitle overlay] → scale → sharpen → fps
///   → burned text subtitles → custom → GPU upload
/// Trim is NOT here: it is applied on the output, after everything (see JobCompiler).
/// </summary>
public static class FilterBuilder
{
    public const string BurnPlaceholder = "{burn}";

    public static List<string> VideoPre(VideoStream v, VideoFilterSettings f, HdrPlan hdr)
    {
        var chain = new List<string>();
        switch (f.Deinterlace)
        {
            case DeinterlaceMode.Auto when v.IsInterlaced:
                chain.Add("bwdif=mode=send_frame:parity=auto:deint=interlaced");
                break;
            case DeinterlaceMode.Force:
                chain.Add("bwdif=mode=send_frame:parity=auto:deint=all");
                break;
        }
        if (Crop(v, f.Crop) is { } crop) chain.Add(crop);
        if (hdr.Filter is { } tm) chain.Add(tm);
        switch (f.Denoise)
        {
            case FilterStrength.Light: chain.Add("hqdn3d=2:1.5:3:2.25"); break;
            case FilterStrength.Medium: chain.Add("hqdn3d=4:3:6:4.5"); break;
            case FilterStrength.Strong: chain.Add("hqdn3d=7:5.5:10:8"); break;
        }
        switch (f.Deband)
        {
            case FilterStrength.Light: chain.Add("deband=1thr=0.015:2thr=0.015:3thr=0.015:range=12:blur=1"); break;
            case FilterStrength.Medium: chain.Add("deband=1thr=0.025:2thr=0.025:3thr=0.025:range=16:blur=1"); break;
            case FilterStrength.Strong: chain.Add("deband=1thr=0.04:2thr=0.04:3thr=0.04:range=22:blur=1"); break;
        }
        return chain;
    }

    public static List<string> VideoPost(VideoStream croppedVideo, JobSettings s, string? burnTextFilter)
    {
        var f = s.VideoFilters;
        var chain = new List<string>();
        if (ScaleCalculator.Compute(croppedVideo, s.ProfileDefinition.UsesResolution ? s.Resolution : 0) is { } scale)
            chain.Add(ScaleCalculator.ToFilter(scale));
        switch (f.Sharpen)
        {
            case FilterStrength.Light: chain.Add("cas=strength=0.3"); break;
            case FilterStrength.Medium: chain.Add("cas=strength=0.5"); break;
            case FilterStrength.Strong: chain.Add("cas=strength=0.8"); break;
        }
        if (f.Fps is > 0 and var fps) chain.Add("fps=" + fps.ToString("0.###", CultureInfo.InvariantCulture));
        if (burnTextFilter is not null) chain.Add(burnTextFilter);
        if (!string.IsNullOrWhiteSpace(f.Custom)) chain.Add(f.Custom.Trim().Trim(','));
        return chain;
    }

    /// <summary>Crop filter with even values (4:2:0 needs them), or null.</summary>
    public static string? Crop(VideoStream v, CropSettings? c)
    {
        if (c is null || c.IsEmpty) return null;
        int l = Even(c.Left), t = Even(c.Top), r = Even(c.Right), b = Even(c.Bottom);
        int w = v.Width - l - r, h = v.Height - t - b;
        if (w < 16 || h < 16) return null;
        return $"crop={w}:{h}:{l}:{t}";
    }

    /// <summary>The video as displayed (rotation applied): width/height swapped for 90°/270°.</summary>
    public static VideoStream Oriented(VideoStream v, VideoFilterSettings f)
    {
        int rot = f.EffectiveRotation(v.Rotation);
        if (rot is not (90 or 270)) return v;
        return new VideoStream
        {
            Index = v.Index, CodecName = v.CodecName, Kind = v.Kind, Width = v.Height, Height = v.Width,
            SampleAspectRatio = v.SampleAspectRatio > 0 ? 1 / v.SampleAspectRatio : 1, FrameRate = v.FrameRate, FrameCount = v.FrameCount,
            FieldOrder = v.FieldOrder, PixelFormat = v.PixelFormat, BitDepth = v.BitDepth, Hdr = v.Hdr,
            ColorTransfer = v.ColorTransfer, ColorPrimaries = v.ColorPrimaries, Rotation = 0,
        };
    }

    /// <summary>Input options that set the display matrix of the video stream (only when overriding).</summary>
    public static List<string> OrientationInputArgs(VideoStream v, VideoFilterSettings f)
    {
        if (!f.OverridesOrientation) return [];
        string spec = ":" + v.Index.ToString(CultureInfo.InvariantCulture);
        int ccw = (360 - f.EffectiveRotation(v.Rotation)) % 360;
        // Flips reset the rotation unless it is given too: always pass it.
        var a = new List<string> { "-display_rotation" + spec, ccw.ToString(CultureInfo.InvariantCulture) };
        if (f.FlipHorizontal) a.AddRange(["-display_hflip" + spec]);
        if (f.FlipVertical) a.AddRange(["-display_vflip" + spec]);
        return a;
    }

    /// <summary>The video as seen after cropping (for the resize rules).</summary>
    public static VideoStream AfterCrop(VideoStream v, CropSettings? c)
    {
        if (Crop(v, c) is null) return v;
        return new VideoStream
        {
            Index = v.Index, CodecName = v.CodecName, Kind = v.Kind,
            Width = v.Width - Even(c!.Left) - Even(c.Right), Height = v.Height - Even(c.Top) - Even(c.Bottom),
            SampleAspectRatio = v.SampleAspectRatio, FrameRate = v.FrameRate, FrameCount = v.FrameCount,
            FieldOrder = v.FieldOrder, PixelFormat = v.PixelFormat, BitDepth = v.BitDepth, Hdr = v.Hdr,
            ColorTransfer = v.ColorTransfer, ColorPrimaries = v.ColorPrimaries, Rotation = v.Rotation,
        };
    }

    /// <summary>Audio filter chain for one track, or null when nothing is needed.</summary>
    public static string? Audio(AudioFilterSettings a, string encoder, int channels)
    {
        var chain = new List<string>();
        if (Math.Abs(a.GainDb) > 0.01) chain.Add("volume=" + a.GainDb.ToString("0.##", CultureInfo.InvariantCulture) + "dB");
        if (a.Normalize)
        {
            chain.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            // loudnorm works at 192 kHz internally: come back to a normal rate.
            chain.Add("aresample=" + (a.SampleRate ?? 48000).ToString(CultureInfo.InvariantCulture));
        }
        else if (a.SampleRate is > 0) chain.Add("aresample=" + a.SampleRate.Value.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(a.Custom)) chain.Add(a.Custom.Trim().Trim(','));
        // libopus rejects "5.1(side)" and similar layouts.
        if (encoder == "libopus") chain.Add(channels > 2 ? "aformat=channel_layouts=7.1|5.1|4.0|stereo|mono" : "aformat=channel_layouts=stereo|mono");
        return chain.Count == 0 ? null : string.Join(',', chain);
    }

    private static int Even(int x) => Math.Max(0, x) & ~1;
}
