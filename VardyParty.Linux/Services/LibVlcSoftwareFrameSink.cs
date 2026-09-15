using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace VardyParty.Linux.Services;

/// <summary>
/// Pushes LibVLC software frames (RV32 / BGRA) into an Avalonia presenter so
/// MAUI chrome can sit on the same scene graph as the picture.
///
/// Cannot be used together with LibVLCSharp's native VideoView: that
/// control owns an X11/Wayland child and fights callback vout.
/// Attach before Play; do not pin <c>--vout=x11</c> on the same instance.
/// </summary>
public sealed class LibVlcSoftwareFrameSink : IDisposable
{
    readonly IVideoFramePresenter _presenter;
    readonly int _maxFrameWidth;
    readonly int _minPresentIntervalMs;
    readonly MediaPlayer.LibVLCVideoLockCb _lock;
    readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    readonly MediaPlayer.LibVLCVideoFormatCb _format;
    readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    readonly LibVlcFramePresentGate _gate = new();
    readonly object _bufferGate = new();

    nint _alignedBuffer;
    int _bufferBytes;
    int _width;
    int _height;
    int _pitch;
    bool _disposed;
    bool _attached;
    byte[] _copy = [];

    public LibVlcSoftwareFrameSink(
        IVideoFramePresenter presenter,
        int maxFrameWidth = 0,
        int minPresentIntervalMs = 0)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _maxFrameWidth = maxFrameWidth;
        _minPresentIntervalMs = minPresentIntervalMs;
        _lock = OnLock;
        _display = OnDisplay;
        _format = OnFormat;
        _cleanup = OnCleanup;
    }

    public void Attach(MediaPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_attached)
        {
            throw new InvalidOperationException("Frame sink is already attached.");
        }

        player.SetVideoFormatCallbacks(_format, _cleanup);
        player.SetVideoCallbacks(_lock, null, _display);
        _attached = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FreeAlignedBuffer();
    }

    uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        _ = opaque;
        LibVlcFrameGeometry.WriteRv32Chroma(chroma);

        var capped = LibVlcFrameGeometry.CapMaxWidth(width, height, _maxFrameWidth);
        width = capped.Width;
        height = capped.Height;

        var w = (int)width;
        var h = (int)height;
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        var pitch = LibVlcFrameGeometry.Pitch(w);
        var alignedLines = LibVlcFrameGeometry.Lines(h);
        pitches = (uint)pitch;
        lines = (uint)alignedLines;

        lock (_bufferGate)
        {
            _width = w;
            _height = h;
            _pitch = pitch;
            EnsureAlignedBuffer(LibVlcFrameGeometry.BufferByteLength(w, h));
            EnsureCopyBuffer();
        }

        return 1;
    }

    void OnCleanup(ref IntPtr opaque)
    {
        _ = opaque;
        FreeAlignedBuffer();
    }

    IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        _ = opaque;
        lock (_bufferGate)
        {
            if (_alignedBuffer == 0)
            {
                return IntPtr.Zero;
            }

            Marshal.WriteIntPtr(planes, _alignedBuffer);
            return _alignedBuffer;
        }
    }

    void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        _ = opaque;
        _ = picture;
        if (_disposed)
        {
            return;
        }

        if (!_gate.TryBeginPresent(_minPresentIntervalMs))
        {
            return;
        }

        int width;
        int height;
        int pitch;
        lock (_bufferGate)
        {
            if (_alignedBuffer == 0 || _width <= 0 || _height <= 0)
            {
                _gate.EndPresent();
                return;
            }

            width = _width;
            height = _height;
            pitch = _pitch;
            EnsureCopyBuffer();
            Marshal.Copy(_alignedBuffer, _copy, 0, height * pitch);
        }

        var snapshot = _copy;
        _presenter.RequestPresent(() =>
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                _presenter.ApplyRv32(snapshot, width, height, pitch);
            }
            finally
            {
                _gate.EndPresent();
            }
        });
    }

    void EnsureAlignedBuffer(int bytes)
    {
        if (_alignedBuffer != 0 && _bufferBytes >= bytes)
        {
            return;
        }

        FreeAlignedBufferUnlocked();
        unsafe
        {
            _alignedBuffer = (nint)NativeMemory.AlignedAlloc((nuint)bytes, 32);
            if (_alignedBuffer == 0)
            {
                throw new OutOfMemoryException("Could not allocate a 32-byte-aligned LibVLC frame buffer.");
            }

            NativeMemory.Clear((void*)_alignedBuffer, (nuint)bytes);
        }

        _bufferBytes = bytes;
    }

    void EnsureCopyBuffer()
    {
        var needed = Math.Max(_bufferBytes, _height * _pitch);
        if (_copy.Length < needed)
        {
            _copy = new byte[needed];
        }
    }

    void FreeAlignedBuffer()
    {
        lock (_bufferGate)
        {
            FreeAlignedBufferUnlocked();
        }
    }

    void FreeAlignedBufferUnlocked()
    {
        if (_alignedBuffer == 0)
        {
            return;
        }

        unsafe
        {
            NativeMemory.AlignedFree((void*)_alignedBuffer);
        }

        _alignedBuffer = 0;
        _bufferBytes = 0;
    }
}
