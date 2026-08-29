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

    // Two-level cache: DISPLAY bytes (PNG for rasterised SVGs, original bytes
    // otherwise) keyed by source, and ImageSources wrapping them. The byte
    // level is exposed via LoadRemoteBytesAsync/LoadLocalBytesAsync so
    // MAUI-less surfaces (the Android video activity's native match-event
    // banner) render the SAME artwork from the SAME single fetch.
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _byteCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    public SkiaBadgeImageLoader(ILogger<SkiaBadgeImageLoader>? logger = null)
    {
        _logger = logger;
    }

    public Task<ImageSource?> LoadRemoteAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<ImageSource?>(null);
        return _cache.GetOrAdd(url, key => WrapAsImageSourceAsync(LoadRemoteBytesAsync(key)));
    }

    public Task<ImageSource?> LoadLocalAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<ImageSource?>(null);
        return _cache.GetOrAdd(path, key => WrapAsImageSourceAsync(LoadLocalBytesAsync(key)));
    }

    public Task<byte[]?> LoadRemoteBytesAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<byte[]?>(null);
        return _byteCache.GetOrAdd(url, LoadRemoteBytesCoreAsync);
    }

    public Task<byte[]?> LoadLocalBytesAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<byte[]?>(null);
        return _byteCache.GetOrAdd(path, LoadLocalBytesCoreAsync);
    }

    private async Task<byte[]?> LoadRemoteBytesCoreAsync(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            var extension = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? Path.GetExtension(uri.AbsolutePath)
                : Path.GetExtension(url);
            return ToDisplayBytes(bytes, extension, url);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load remote image {Url}", url);
            return null;
        }
    }

    private async Task<byte[]?> LoadLocalBytesCoreAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            return ToDisplayBytes(bytes, Path.GetExtension(path), path);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to load local image {Path}", path);
            return null;
        }
    }

    private static async Task<ImageSource?> WrapAsImageSourceAsync(Task<byte[]?> bytesTask)
    {
        var bytes = await bytesTask.ConfigureAwait(false);
        return bytes == null ? null : ToImageSource(bytes);
    }

    private byte[]? ToDisplayBytes(byte[] bytes, string extension, string source) =>
        extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            ? RasterizeSvg(bytes, source)
            : bytes;

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
            // Dark brandlogos (near-black OR brand purple like PL #3d195b) →
            // off-white for the dark homepage. White BBC crests get a dark
            // circular backing so they still read on light badge plates.
            BadgeCrestContrast.LightenDarkInk(bitmap);
            BadgeCrestContrast.ApplyDarkBackingIfLightDominant(bitmap);
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

    // A fresh stream per call: MAUI may read the source more than once.
    private static ImageSource ToImageSource(byte[] bytes) =>
        ImageSource.FromStream(() => new MemoryStream(bytes));
}
