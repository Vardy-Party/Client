using SkiaSharp;

namespace VardyParty.HomeUi;

/// <summary>
/// Contrast helpers for league marks and team crests after SVG rasterise.
/// <list type="bullet">
/// <item>Dark brandlogos (near-black, PL purple <c>#3d195b</c>, …) → off-white
/// for the dark homepage league rail.</item>
/// <item>Pure-white BBC crests (Juventus) → dark circular backing so they read
/// on light badge plates.</item>
/// </list>
/// </summary>
public static class BadgeCrestContrast
{
    /// <summary>Dark plate colour matched to homepage chrome (<c>#1A2233</c>).</summary>
    public const byte DarkR = 0x1A;
    public const byte DarkG = 0x22;
    public const byte DarkB = 0x33;

    /// <summary>Off-white ink for lightened dark marks on the homepage.</summary>
    public const byte LightInkR = 245;
    public const byte LightInkG = 246;
    public const byte LightInkB = 248;

    /// <summary>
    /// Relative luminance threshold. PL purple (#3d195b) ≈ 37; near-black
    /// brandlogos are lower. The old R+G+B&lt;140 test missed purple (sum 177).
    /// </summary>
    public const float DarkLuminanceMax = 80f;

    /// <summary>
    /// Max channel spread for "ink" (near-monochrome). Keeps Arsenal red /
    /// Chelsea blue from being bleach-rewritten while still catching PL purple
    /// (chroma ≈ 66) and near-black brandlogos.
    /// </summary>
    public const int DarkChromaMax = 90;

    public static readonly SKColor DarkBacking = new(DarkR, DarkG, DarkB, 255);

    public readonly record struct Rgba(byte R, byte G, byte B, byte A);

    /// <summary>Rec. 709 relative luminance (0–255 scale).</summary>
    public static float Luminance(byte r, byte g, byte b) =>
        0.2126f * r + 0.7152f * g + 0.0722f * b;

    public static int Chroma(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return max - min;
    }

    public static bool IsDarkInk(byte r, byte g, byte b) =>
        Luminance(r, g, b) < DarkLuminanceMax && Chroma(r, g, b) <= DarkChromaMax;

    /// <summary>
    /// True when opaque samples are mostly near-white (≈80%+). Colourful
    /// crests return false so their light plate treatment is left alone.
    /// </summary>
    public static bool IsLightDominant(int width, int height, Func<int, int, Rgba> getPixel)
    {
        ArgumentNullException.ThrowIfNull(getPixel);
        if (width <= 0 || height <= 0) return false;

        var light = 0;
        var opaque = 0;
        for (var y = 0; y < height; y += 4)
        {
            for (var x = 0; x < width; x += 4)
            {
                var pixel = getPixel(x, y);
                if (pixel.A < 40) continue;
                opaque++;
                if (pixel.R >= 200 && pixel.G >= 200 && pixel.B >= 200)
                {
                    light++;
                }
            }
        }

        return opaque > 0 && light * 5 >= opaque * 4;
    }

    /// <summary>
    /// True when opaque samples are mostly dark by luminance (≈50%+). Used for
    /// league wordmarks that vanish on the dark homepage.
    /// </summary>
    public static bool IsDarkDominant(int width, int height, Func<int, int, Rgba> getPixel)
    {
        ArgumentNullException.ThrowIfNull(getPixel);
        if (width <= 0 || height <= 0) return false;

        var dark = 0;
        var opaque = 0;
        for (var y = 0; y < height; y += 4)
        {
            for (var x = 0; x < width; x += 4)
            {
                var pixel = getPixel(x, y);
                if (pixel.A < 40) continue;
                opaque++;
                if (IsDarkInk(pixel.R, pixel.G, pixel.B)) dark++;
            }
        }

        return opaque > 0 && dark * 2 >= opaque;
    }

    /// <summary>
    /// If the crest/mark is dark-dominant, recolor dark opaque pixels to
    /// off-white (keeps alpha). No-op for colourful or light marks.
    /// </summary>
    public static void LightenDarkInk(
        int width,
        int height,
        Func<int, int, Rgba> getPixel,
        Action<int, int, Rgba> setPixel)
    {
        ArgumentNullException.ThrowIfNull(getPixel);
        ArgumentNullException.ThrowIfNull(setPixel);
        if (!IsDarkDominant(width, height, getPixel)) return;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = getPixel(x, y);
                if (pixel.A < 40) continue;
                if (!IsDarkInk(pixel.R, pixel.G, pixel.B)) continue;
                setPixel(x, y, new Rgba(LightInkR, LightInkG, LightInkB, pixel.A));
            }
        }
    }

    /// <summary>
    /// If the buffer is light-dominant, fill transparent pixels inside the
    /// crest's bounding circle with the dark backing colour.
    /// </summary>
    public static void ApplyDarkBackingIfLightDominant(
        int width,
        int height,
        Func<int, int, Rgba> getPixel,
        Action<int, int, Rgba> setPixel)
    {
        ArgumentNullException.ThrowIfNull(getPixel);
        ArgumentNullException.ThrowIfNull(setPixel);
        if (!IsLightDominant(width, height, getPixel)) return;

        var minX = width;
        var minY = height;
        var maxX = 0;
        var maxY = 0;
        var found = false;
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                if (getPixel(x, y).A < 40) continue;
                found = true;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (!found) return;

        var cx = (minX + maxX) * 0.5f;
        var cy = (minY + maxY) * 0.5f;
        // Cover the opaque bbox with a little margin so thin white strokes
        // at the edge still sit on dark, not on the light plate behind.
        var radius = Math.Max(maxX - minX, maxY - minY) * 0.58f;
        var radiusSq = radius * radius;
        var dark = new Rgba(DarkR, DarkG, DarkB, 255);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (getPixel(x, y).A >= 40) continue;
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy <= radiusSq)
                {
                    setPixel(x, y, dark);
                }
            }
        }
    }

    /// <summary>Skia wrapper used by <see cref="SkiaBadgeImageLoader"/>.</summary>
    public static void ApplyDarkBackingIfLightDominant(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ApplyDarkBackingIfLightDominant(
            bitmap.Width,
            bitmap.Height,
            (x, y) =>
            {
                var p = bitmap.GetPixel(x, y);
                return new Rgba(p.Red, p.Green, p.Blue, p.Alpha);
            },
            (x, y, p) => bitmap.SetPixel(x, y, new SKColor(p.R, p.G, p.B, p.A)));
    }

    /// <summary>Skia wrapper used by <see cref="SkiaBadgeImageLoader"/>.</summary>
    public static void LightenDarkInk(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        LightenDarkInk(
            bitmap.Width,
            bitmap.Height,
            (x, y) =>
            {
                var p = bitmap.GetPixel(x, y);
                return new Rgba(p.Red, p.Green, p.Blue, p.Alpha);
            },
            (x, y, p) => bitmap.SetPixel(x, y, new SKColor(p.R, p.G, p.B, p.A)));
    }
}
