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

    public bool TryBeginPresent() => Interlocked.CompareExchange(ref _posted, 1, 0) == 0;

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
