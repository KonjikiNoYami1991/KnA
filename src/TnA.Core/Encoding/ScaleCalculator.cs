using TnA.Core.Media;

namespace TnA.Core.Encoding;

public sealed record ScaleResult(int Width, int Height, bool SquarePixels);

/// <summary>
/// Resize rules inherited from TnA 0.9.x, minus its bugs:
///  • never upscale;
///  • display aspect ≥ 4:3 → the target width (height × 16/9) bounds the picture, otherwise the height does;
///  • output dimensions are always even (required by 4:2:0 encoders);
///  • anamorphic sources become square pixels when they get resized.
/// </summary>
public static class ScaleCalculator
{
    /// <returns>Null when no scaling is needed.</returns>
    public static ScaleResult? Compute(VideoStream v, int targetHeight)
    {
        if (v.Width <= 0 || v.Height <= 0) return null;
        double dar = v.DisplayAspectRatio;
        if (dar <= 0) dar = (double)v.Width / v.Height;

        if (targetHeight > 0)
        {
            int targetWidth = Even(targetHeight * 16.0 / 9.0);
            if (dar >= 4.0 / 3.0 - 0.001)
            {
                if (v.DisplayWidth > targetWidth + 1)
                    return new ScaleResult(targetWidth, Math.Max(2, Even(targetWidth / dar)), true);
            }
            else if (v.Height > targetHeight)
            {
                int h = Even(targetHeight);
                return new ScaleResult(Math.Max(2, Even(h * dar)), h, true);
            }
        }

        // No downscale: only fix odd dimensions (e.g. 1555×813 → 1554×812).
        int w = v.Width & ~1, hh = v.Height & ~1;
        return w == v.Width && hh == v.Height ? null : new ScaleResult(w, hh, false);
    }

    public static string ToFilter(ScaleResult r) =>
        r.SquarePixels ? $"scale={r.Width}:{r.Height}:flags=lanczos,setsar=1" : $"scale={r.Width}:{r.Height}:flags=lanczos";

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2);
}
