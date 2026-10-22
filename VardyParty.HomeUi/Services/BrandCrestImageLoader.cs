using SkiaSharp;
using Svg.Skia;

namespace VardyParty.HomeUi;

/// <summary>
/// Loads the metallic brand crest for <c>BrandLogoView</c> as two layers.
/// The disc (sphere highlight and rim) stays fixed. The mark (diamond,
/// panels, seams) is the only layer the view rotates. Prefers the
/// pre-rasterised PNGs embedded beside the SVGs so first paint never depends
/// on Svg.Skia natives (field: empty spinning ring on WinUI/Android when
/// StreamImageSource nested under a yawing plate failed to show the crest).
/// SVG remains the fallback when a PNG embed is missing.
/// </summary>
public static class BrandCrestImageLoader
{
    /// <summary>Raster size used when falling back to SVG → PNG.</summary>
    private const int RasterPixels = 256;

    private static readonly CrestLayer Disc = new(
        "brand_crest_disc.png",
        "brand_crest_disc.svg",
        "vardy_brand_crest_disc_v1.png");

    private static readonly CrestLayer Mark = new(
        "brand_crest_mark.png",
        "brand_crest_mark.svg",
        "vardy_brand_crest_mark_v1.png");

    /// <summary>Static sphere fill and rim. Do not rotate this source.</summary>
    public static ImageSource? GetDisc() => Disc.GetImage();

    /// <summary>Transparent diamond, panels, and seams. This is the spinning layer.</summary>
    public static ImageSource? GetMark() => Mark.GetImage();

    /// <summary>Same as <see cref="GetDisc"/> but never blocks the caller on I/O.</summary>
    public static Task<ImageSource?> GetDiscAsync(CancellationToken cancellationToken = default) =>
        Disc.GetImageAsync(cancellationToken);

    /// <summary>Same as <see cref="GetMark"/> but never blocks the caller on I/O.</summary>
    public static Task<ImageSource?> GetMarkAsync(CancellationToken cancellationToken = default) =>
        Mark.GetImageAsync(cancellationToken);

    private sealed class CrestLayer
    {
        private readonly string _pngSuffix;
        private readonly string _svgSuffix;
        private readonly string _cacheFileName;
        private readonly Lazy<byte[]?> _png;
        private readonly object _fileGate = new();
        private string? _cachedFilePath;

        public CrestLayer(string pngSuffix, string svgSuffix, string cacheFileName)
        {
            _pngSuffix = pngSuffix;
            _svgSuffix = svgSuffix;
            _cacheFileName = cacheFileName;
            _png = new Lazy<byte[]?>(LoadPngBytes, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public ImageSource? GetImage()
        {
            var png = _png.Value;
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

        public Task<ImageSource?> GetImageAsync(CancellationToken cancellationToken) =>
            Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return GetImage();
            }, cancellationToken);

        private string? EnsureCacheFile(byte[] png)
        {
            lock (_fileGate)
            {
                if (_cachedFilePath != null && File.Exists(_cachedFilePath))
                {
                    return _cachedFilePath;
                }

                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "VardyParty");
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, _cacheFileName);
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

        private byte[]? LoadPngBytes()
        {
            try
            {
                var png = ReadEmbedded(_pngSuffix);
                if (png is { Length: > 0 })
                {
                    return png;
                }

                return RenderSvgFallback();
            }
            catch
            {
                return null;
            }
        }

        private byte[]? RenderSvgFallback()
        {
            try
            {
                var svgBytes = ReadEmbedded(_svgSuffix);
                if (svgBytes == null)
                {
                    return null;
                }

                using var stream = new MemoryStream(svgBytes);
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
                    Math.Max(1, (int)Math.Ceiling(bounds.Height * scale)),
                    SKColorType.Rgba8888,
                    SKAlphaType.Premul);

                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.Scale(scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
                canvas.Flush();

                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data?.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static byte[]? ReadEmbedded(string suffix)
        {
            var assembly = typeof(BrandCrestImageLoader).Assembly;
            var name = Array.Find(
                assembly.GetManifestResourceNames(),
                n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                return null;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.Length == 0 ? null : ms.ToArray();
        }
    }
}
