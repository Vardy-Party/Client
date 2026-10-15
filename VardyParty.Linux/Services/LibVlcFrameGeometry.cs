using System.Runtime.InteropServices;

namespace VardyParty.Linux.Services;

/// <summary>
/// Pitch/chroma math for the LibVLC software-callback path (RV32). LibVLC
/// wants 32-byte aligned pitches and lines so SIMD converters do not assume
/// a tight packed buffer.
/// </summary>
public static class LibVlcFrameGeometry
{
    public const string Rv32Chroma = "RV32";

    public static uint Align32(uint value) => (value + 31u) & ~31u;

    public static uint Pitch(uint width) => Align32(checked(width * 4u));

    public static uint Lines(uint height) => Align32(height);

    public static int Pitch(int width)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        return (int)Pitch((uint)width);
    }

    public static int Lines(int height)
    {
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        return (int)Lines((uint)height);
    }

    public static int BufferByteLength(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        return BufferByteLength((uint)width, (uint)height);
    }

    public static int BufferByteLength(uint width, uint height)
    {
        var bytes = (long)Pitch(width) * Lines(height);
        if (bytes <= 0 || bytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame buffer is empty or too large.");
        }

        return (int)bytes;
    }

    /// <summary>
    /// Shrinks a frame so its width is at most <paramref name="maxWidth"/>,
    /// keeping aspect. Even height for planar converters. No-op when
    /// <paramref name="maxWidth"/> is 0 or the frame is already smaller.
    /// </summary>
    public static (uint Width, uint Height) CapMaxWidth(uint width, uint height, int maxWidth)
    {
        if (maxWidth <= 0 || width == 0 || height == 0 || width <= (uint)maxWidth)
            return (width, height);

        var scale = (double)maxWidth / width;
        var cappedHeight = (uint)Math.Max(2, Math.Round(height * scale));
        cappedHeight &= ~1u;
        if (cappedHeight == 0)
            cappedHeight = 2;
        return ((uint)maxWidth, cappedHeight);
    }

    /// <summary>Writes the four-character RV32 chroma tag into LibVLC's chroma pointer.</summary>
    public static void WriteRv32Chroma(IntPtr chroma)
    {
        if (chroma == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(chroma));
        }

        Marshal.WriteByte(chroma, 0, (byte)'R');
        Marshal.WriteByte(chroma, 1, (byte)'V');
        Marshal.WriteByte(chroma, 2, (byte)'3');
        Marshal.WriteByte(chroma, 3, (byte)'2');
    }
}

/// <summary>
/// One-in-flight UI present. Display callbacks always copy into the latest
/// staging buffer; extra frames drop their UI post so the dispatcher cannot
/// pile up.
/// </summary>
public sealed class LibVlcFramePresentGate
{
    private int _posted;
    private long _nextAllowedMs;

    public bool TryBeginPresent(int minIntervalMs = 0)
    {
        if (minIntervalMs > 0)
        {
            var now = Environment.TickCount64;
            if (now < Volatile.Read(ref _nextAllowedMs))
                return false;
        }

        if (Interlocked.CompareExchange(ref _posted, 1, 0) != 0)
            return false;

        if (minIntervalMs > 0)
            Volatile.Write(ref _nextAllowedMs, Environment.TickCount64 + minIntervalMs);

        return true;
    }

    public void EndPresent() => Interlocked.Exchange(ref _posted, 0);
}

/// <summary>
/// UI-thread consumer of an RV32 frame. Implementations marshal
/// <see cref="RequestPresent"/> onto the Avalonia/MAUI dispatcher and
/// apply pixels on that thread via <see cref="ApplyRv32"/>.
/// </summary>
public interface IVideoFramePresenter
{
    void RequestPresent(Action applyOnUiThread);

    void ApplyRv32(byte[] pixels, int width, int height, int stride);
}
