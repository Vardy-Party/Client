#if EMBEDDED_DESKTOP_VIDEO
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VardyParty.Desktop.Services;

namespace VardyParty.Desktop.Controls;

/// <summary>
/// MAUI view that hosts LibVLC software frames as an Avalonia <c>Image</c>
/// in the same scene graph as the homepage chrome. Handler
/// (<see cref="VideoHostViewHandler"/>) realizes the Image; this view
/// implements <see cref="IVideoFramePresenter"/> so the player can push
/// RV32 frames onto it.
///
/// Because the picture is Avalonia-drawn (not a native child window),
/// overlapping MAUI controls are visible and hit-testable — true overlays.
///
/// The whole type compiles out with <c>-p:EmbeddedDesktopVideo=false</c>.
/// </summary>
public sealed class VideoHostView : View, IVideoFramePresenter
{
    Avalonia.Controls.Image? _image;
    WriteableBitmap? _bitmap;
    int _bitmapWidth;
    int _bitmapHeight;

    internal void AttachPlatformImage(Avalonia.Controls.Image image)
    {
        _image = image;
        image.Stretch = Avalonia.Media.Stretch.Uniform;
    }

    internal void DetachPlatformImage()
    {
        _image = null;
        _bitmap = null;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
    }

    public void RequestPresent(Action applyOnUiThread)
    {
        ArgumentNullException.ThrowIfNull(applyOnUiThread);
        Dispatcher.Dispatch(applyOnUiThread);
    }

    public void ApplyRv32(byte[] pixels, int width, int height, int stride)
    {
        if (_image == null || pixels == null || width <= 0 || height <= 0 || stride <= 0)
        {
            return;
        }

        if (_bitmap == null || _bitmapWidth != width || _bitmapHeight != height)
        {
            _bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Opaque);
            _bitmapWidth = width;
            _bitmapHeight = height;
            _image.Source = _bitmap;
        }

        var rowBytes = Math.Min(stride, width * 4);
        using (var fb = _bitmap.Lock())
        {
            for (var y = 0; y < height; y++)
            {
                var destStride = fb.RowBytes;
                var copy = Math.Min(rowBytes, destStride);
                Marshal.Copy(pixels, y * stride, fb.Address + (y * destStride), copy);
            }
        }

        _image.InvalidateVisual();
    }

    public void ClearFrame()
    {
        if (_image != null)
        {
            _image.Source = null;
        }

        _bitmap = null;
        _bitmapWidth = 0;
        _bitmapHeight = 0;
    }
}
#endif
