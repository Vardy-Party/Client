using SkiaSharp;

namespace VardyParty.HomeUi;

/// <summary>
/// Contrast helpers for team crests rasterised onto transparent canvases.
/// BBC SVGs such as Juventus ship as pure <c>#fff</c> strokes; on the match-
/// card light plate they disappear. When a crest is light-dominant we paint a
/// dark circular backing under the opaque ink so the same PNG stays readable
/// on light plates and on bare dark toast hosts.
/// </summary>
public static class BadgeCrestContrast
{
    /// <summary>Dark plate colour matched to homepage chrome (<c>#1A2233</c>).</summary>
    public const byte DarkR = 0x1A;
    public const byte DarkG = 0x22;
    public const byte DarkB = 0x33;

    public static readonly SKColor DarkBacking = new(DarkR, DarkG, DarkB, 255);

    public readonly record struct Rgba(byte R, byte G, byte B, byte A);

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
}
