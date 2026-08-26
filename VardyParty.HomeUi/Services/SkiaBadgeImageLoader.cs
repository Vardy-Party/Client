using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Svg.Skia;

namespace VardyParty.HomeUi;

/// <summary>
/// Default <see cref="IBadgeImageLoader"/>: HTTP + file loading with an
/// in-memory cache keyed by source. SVGs (the BBC badge format) are rasterised
/// through Svg.Skia to PNG bytes so MAUI's <see cref="ImageSource"/> can show
/// them on every backend; bitmaps pass straight through. Concurrent requests
/// for the same source share one task, so a poll cycle never fetches the same
/// badge twice.
/// </summary>
public sealed class SkiaBadgeImageLoader : IBadgeImageLoader
{
    /// <summary>Raster size for SVG badges; big enough for the TV layout's 68dip badge at 2x scale.</summary>
    private const int SvgRasterPixels = 160;

    private static readonly HttpClient Http = new();
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public SkiaBadgeImageLoader(ILogger<SkiaBadgeImageLoader>? logger = null)
    {
        _logger = logger;
    }

    public Task<ImageSource?> LoadRemoteAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource?>(null);
        return _cache.GetOrAdd(url, LoadRemoteCoreAsync);
    }

    public Task<ImageSource?> LoadLocalAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<ImageSource?>(null);
        return _cache.GetOrAdd(path, LoadLocalCoreAsync);
    }

    private async Task<ImageSource?> LoadRemoteCoreAsync(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            var extension = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? Path.GetExtension(uri.AbsolutePath)
                : Path.GetExtension(url);
            return Decode(bytes, extension, url);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load remote image {Url}", url);
            return null;
        }
    }

    private async Task<ImageSource?> LoadLocalCoreAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return Decode(bytes, Path.GetExtension(path), path);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load local image {Path}", path);
            return null;
        }
    }

    private ImageSource? Decode(byte[] bytes, string extension, string source)
    {
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var png = RasterizeSvg(bytes, source);
            return png == null ? null : ToImageSource(png);
        }

        return ToImageSource(bytes);
    }

    private byte[]? RasterizeSvg(byte[] bytes, string source)
    {
        try
        {
            using var svg = new SKSvg();
            using var stream = new MemoryStream(bytes);
            var picture = svg.Load(stream);
            if (picture == null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            {
                return null;
            }

            var bounds = picture.CullRect;
            var scale = SvgRasterPixels / Math.Max(bounds.Width, bounds.Height);
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

            using var snapshot = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(snapshot);
            LightenDarkMonochrome(bitmap);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SVG rasterise failed for {Source}", source);
            return null;
        }
    }

    /// <summary>
    /// Brandlogos league marks are often near-black (#221f1f). On the dark
    /// homepage that looks like a missing icon. Recolor dark opaque pixels
    /// to off-white and keep alpha so the silhouette still reads.
    /// </summary>
    private static void LightenDarkMonochrome(SKBitmap bitmap)
    {
        var dark = 0;
        var opaque = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 40) continue;
                opaque++;
                if (pixel.Red + pixel.Green + pixel.Blue < 140) dark++;
            }
        }

        if (opaque == 0 || dark * 2 < opaque) return;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 40) continue;
                if (pixel.Red + pixel.Green + pixel.Blue >= 140) continue;
                bitmap.SetPixel(x, y, new SKColor(245, 246, 248, pixel.Alpha));
            }
        }
    }

    // A fresh stream per call: MAUI may read the source more than once.
    private static ImageSource ToImageSource(byte[] bytes) =>
        ImageSource.FromStream(() => new MemoryStream(bytes));
}
