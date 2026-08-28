using SkiaSharp;
using Svg.Skia;

namespace VardyParty.HomeUi;

/// <summary>
/// Loads the metallic brand crest for <c>BrandLogoView</c>. Prefers the
/// pre-rasterised PNG embedded beside the SVG so first paint never depends
/// on Svg.Skia natives (field: empty spinning ring on WinUI/Android when
/// StreamImageSource + nested RotationY failed to show the crest). SVG
/// remains as a fallback for builds that omit the PNG.
/// </summary>
public static class BrandCrestImageLoader
{
    /// <summary>Raster size used when falling back to SVG → PNG.</summary>
    private const int RasterPixels = 256;

    private const string CacheFileName = "vardy_brand_crest_v1.png";

    private static readonly Lazy<byte[]?> CrestPng = new(LoadPngBytes, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object FileGate = new();
    private static string? _cachedFilePath;

    public static ImageSource? GetCrest()
    {
        var png = CrestPng.Value;
        if (png == null)
        {
            return null;
        }

        // Prefer a file path: WinUI and Android MAUI Image handlers have been
        // flaky with StreamImageSource for this crest. Avalonia accepted
        // FromStream; FromFile works on every head we ship.
        var path = EnsureCacheFile(png);
        if (path != null)
        {
            return ImageSource.FromFile(path);
        }

        return ImageSource.FromStream(() => new MemoryStream(png));
    }

    /// <summary>Same as <see cref="GetCrest"/> but never blocks the caller on I/O.</summary>
    public static Task<ImageSource?> GetCrestAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetCrest();
        }, cancellationToken);

    private static string? EnsureCacheFile(byte[] png)
    {
        lock (FileGate)
        {
            if (_cachedFilePath != null && File.Exists(_cachedFilePath))
            {
                return _cachedFilePath;
            }

            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "VardyParty");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, CacheFileName);
                if (!File.Exists(path) || new FileInfo(path).Length != png.Length)
                {
                    File.WriteAllBytes(path, png);
                }

                _cachedFilePath = path;
                return path;
            }
            catch
            {
                return null;
            }
        }
    }

    private static byte[]? LoadPngBytes()
    {
        try
        {
            var assembly = typeof(BrandCrestImageLoader).Assembly;
            var pngName = Array.Find(assembly.GetManifestResourceNames(),
                n => n.EndsWith("brand_crest.png", StringComparison.OrdinalIgnoreCase));
            if (pngName != null)
            {
                using var stream = assembly.GetManifestResourceStream(pngName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    if (ms.Length > 0)
                    {
                        return ms.ToArray();
                    }
                }
            }

            return RenderSvgFallback();
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? RenderSvgFallback()
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
            return null;
        }
    }
}
