using SkiaSharp;
using Svg.Skia;

namespace VardyParty.HomeUi;

/// <summary>
/// Loads the embedded metallic brand crest (Resources/brand_crest.svg) and
/// rasterises it once through the same Svg.Skia path the badge loader uses.
/// The PNG bytes are cached for the process lifetime; failures return null so
/// <c>BrandLogoView</c> can simply hide the image layer.
/// </summary>
public static class BrandCrestImageLoader
{
    /// <summary>Raster size: TV layout's 76dip logo at up to ~3x density.</summary>
    private const int RasterPixels = 256;

    private static readonly Lazy<byte[]?> CrestPng = new(Render, LazyThreadSafetyMode.ExecutionAndPublication);

    public static ImageSource? GetCrest()
    {
        var png = CrestPng.Value;
        return png == null ? null : ImageSource.FromStream(() => new MemoryStream(png));
    }

    private static byte[]? Render()
    {
        try
        {
            var assembly = typeof(BrandCrestImageLoader).Assembly;
            var name = Array.Find(assembly.GetManifestResourceNames(),
                n => n.EndsWith("brand_crest.svg", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) return null;

            using var svg = new SKSvg();
            var picture = svg.Load(stream);
            if (picture == null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            {
                return null;
            }

            var bounds = picture.CullRect;
            var scale = RasterPixels / Math.Max(bounds.Width, bounds.Height);
            var info = new SKImageInfo(
                Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)));

            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch
        {
            // No crest is better than a startup crash on an exotic backend.
            return null;
        }
    }
}
