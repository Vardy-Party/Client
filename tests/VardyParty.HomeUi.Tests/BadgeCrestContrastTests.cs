using System;
using VardyParty.HomeUi;
using Xunit;

namespace VardyParty.HomeUi.Tests;

/// <summary>
/// Pure-buffer tests — no SkiaSharp natives required on the CI/Linux host
/// (same constraint as <see cref="BrandCrestImageLoaderTests"/>).
/// </summary>
public class BadgeCrestContrastTests
{
    [Fact]
    public void IsLightDominant_TrueForNearWhiteCrest()
    {
        var buf = NewBuffer(40, 40);
        FillRect(buf, 40, 10, 10, 30, 30, 255, 255, 255, 255);

        Assert.True(BadgeCrestContrast.IsLightDominant(40, 40, Get(buf, 40)));
    }

    [Fact]
    public void IsLightDominant_FalseForColourfulCrest()
    {
        var buf = NewBuffer(40, 40);
        FillRect(buf, 40, 10, 10, 20, 30, 200, 16, 46, 255);
        FillRect(buf, 40, 20, 10, 30, 30, 0, 70, 173, 255);

        Assert.False(BadgeCrestContrast.IsLightDominant(40, 40, Get(buf, 40)));
    }

    [Fact]
    public void ApplyDarkBacking_FillsTransparentInsideLightCrest()
    {
        var buf = NewBuffer(40, 40);
        // White ring — hollow centre must pick up the dark backing.
        for (var y = 8; y < 32; y++)
        {
            for (var x = 8; x < 32; x++)
            {
                var dx = x - 20;
                var dy = y - 20;
                var r2 = dx * dx + dy * dy;
                if (r2 is >= 64 and <= 121)
                {
                    Set(buf, 40, x, y, 255, 255, 255, 255);
                }
            }
        }

        BadgeCrestContrast.ApplyDarkBackingIfLightDominant(40, 40, Get(buf, 40), Set(buf, 40));

        var centre = Get(buf, 40)(20, 20);
        Assert.Equal(BadgeCrestContrast.DarkR, centre.R);
        Assert.Equal(BadgeCrestContrast.DarkG, centre.G);
        Assert.Equal(BadgeCrestContrast.DarkB, centre.B);
        Assert.Equal(255, centre.A);

        var ring = Get(buf, 40)(20, 10);
        Assert.Equal(255, ring.R);
        Assert.Equal(255, ring.G);
        Assert.Equal(255, ring.B);
    }

    [Fact]
    public void ApplyDarkBacking_NoOpForColourfulCrest()
    {
        var buf = NewBuffer(40, 40);
        Set(buf, 40, 20, 20, 200, 16, 46, 255);

        BadgeCrestContrast.ApplyDarkBackingIfLightDominant(40, 40, Get(buf, 40), Set(buf, 40));

        Assert.Equal(0, Get(buf, 40)(10, 10).A);
    }

    [Fact]
    public void IsDarkInk_TrueForPremierLeaguePurple()
    {
        // Official PL purple #3d195b — old R+G+B<140 test missed this (sum 177).
        Assert.True(BadgeCrestContrast.IsDarkInk(0x3d, 0x19, 0x5b));
        Assert.True(BadgeCrestContrast.Luminance(0x3d, 0x19, 0x5b) < BadgeCrestContrast.DarkLuminanceMax);
        Assert.True(BadgeCrestContrast.Chroma(0x3d, 0x19, 0x5b) <= BadgeCrestContrast.DarkChromaMax);
    }

    [Fact]
    public void IsDarkInk_FalseForSaturatedTeamRed()
    {
        Assert.False(BadgeCrestContrast.IsDarkInk(200, 16, 46));
    }

    [Fact]
    public void LightenDarkInk_RecolorsPremierLeaguePurple()
    {
        var buf = NewBuffer(40, 40);
        FillRect(buf, 40, 10, 10, 30, 30, 0x3d, 0x19, 0x5b, 255);

        Assert.True(BadgeCrestContrast.IsDarkDominant(40, 40, Get(buf, 40)));
        BadgeCrestContrast.LightenDarkInk(40, 40, Get(buf, 40), Set(buf, 40));

        var p = Get(buf, 40)(20, 20);
        Assert.Equal(BadgeCrestContrast.LightInkR, p.R);
        Assert.Equal(BadgeCrestContrast.LightInkG, p.G);
        Assert.Equal(BadgeCrestContrast.LightInkB, p.B);
        Assert.Equal(255, p.A);
    }

    [Fact]
    public void LightenDarkInk_NoOpForColourfulCrest()
    {
        var buf = NewBuffer(40, 40);
        FillRect(buf, 40, 10, 10, 20, 30, 200, 16, 46, 255);
        FillRect(buf, 40, 20, 10, 30, 30, 0, 70, 173, 255);

        BadgeCrestContrast.LightenDarkInk(40, 40, Get(buf, 40), Set(buf, 40));

        Assert.Equal(200, Get(buf, 40)(12, 12).R);
        Assert.Equal(0, Get(buf, 40)(22, 12).R);
    }

    private static byte[] NewBuffer(int w, int h) => new byte[w * h * 4];

    private static Func<int, int, BadgeCrestContrast.Rgba> Get(byte[] buf, int w) =>
        (x, y) =>
        {
            var i = (y * w + x) * 4;
            return new BadgeCrestContrast.Rgba(buf[i], buf[i + 1], buf[i + 2], buf[i + 3]);
        };

    private static Action<int, int, BadgeCrestContrast.Rgba> Set(byte[] buf, int w) =>
        (x, y, p) => Set(buf, w, x, y, p.R, p.G, p.B, p.A);

    private static void Set(byte[] buf, int w, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = (y * w + x) * 4;
        buf[i] = r;
        buf[i + 1] = g;
        buf[i + 2] = b;
        buf[i + 3] = a;
    }

    private static void FillRect(
        byte[] buf, int w, int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a)
    {
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                Set(buf, w, x, y, r, g, b, a);
            }
        }
    }
}
