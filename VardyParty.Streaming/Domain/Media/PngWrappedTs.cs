using System.Buffers.Binary;
using System.IO.Compression;

namespace VardyParty.Streaming;

/// <summary>
/// TikTok-CDN "image" HLS segments that actually carry MPEG-TS.
/// <para>Format (captured 27 Oct 2026, cowedd premium1 / castppv via tiktokcdn-us.com):</para>
/// <list type="number">
/// <item>Ordinary PNG: IHDR (seen 512 px wide, 8-bit RGB, colour type 2, no interlace),
/// one zlib stream across many IDAT chunks, IEND. Every row uses a standard PNG
/// filter (0-4 all occur), so the scanlines must be un-filtered.</item>
/// <item>The un-filtered pixel bytes, read row by row, start with the ASCII marker
/// <c>TIKTIKPX</c>, then a 4-byte big-endian payload length.</item>
/// <item>The payload is a gzip member (seen: stored/no-compression deflate) whose
/// output is plain MPEG-TS (188-byte packets, 0x47 sync). Bytes after the payload
/// are padding to fill the last row and are ignored.</item>
/// </list>
/// A PNG without the marker is a real image (placeholder/decoy) and is not playable.
/// No method here throws on malformed input.
/// </summary>
public static class PngWrappedTs
{
    public const int HeaderLength = 12;
    public const int MarkerLength = 8;
    public const int TsPacketSize = 188;

    /// <summary>Hard ceilings so a hostile PNG cannot exhaust memory.</summary>
    public const int MaxPngBytes = 48 * 1024 * 1024;
    public const long MaxPixelBytes = 64L * 1024 * 1024;
    public const int MaxTsBytes = 64 * 1024 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ReadOnlySpan<byte> Marker => "TIKTIKPX"u8;

    public static bool IsPng(ReadOnlySpan<byte> body) =>
        body.Length >= PngSignature.Length && body[..PngSignature.Length].SequenceEqual(PngSignature);

    /// <summary>
    /// Full unwrap. Returns false (never throws) for non-PNG, PNG without the marker,
    /// truncated/corrupt data, or a payload that is not MPEG-TS.
    /// </summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> png, out byte[] ts) =>
        TryUnwrap(png, out ts, out _);

    public static bool TryUnwrap(ReadOnlySpan<byte> png, out byte[] ts, out string failure)
    {
        ts = [];
        try
        {
            if (png.Length > MaxPngBytes)
            {
                failure = "png too large";
                return false;
            }

            if (!TryDecodePixels(png, requireComplete: true, maxPixelBytes: MaxPixelBytes, out var pixels, out failure))
                return false;

            if (!TryReadHeader(pixels, out var payloadLength, out failure))
                return false;

            if (HeaderLength + (long)payloadLength > pixels.Length)
            {
                failure = "payload length exceeds pixel data";
                return false;
            }

            var payload = pixels.AsSpan(HeaderLength, (int)payloadLength);
            if (!TryDecodePayload(payload, requireComplete: true, MaxTsBytes, out ts, out failure))
                return false;

            if (!LooksLikeTransportStream(ts, requireWholePackets: false))
            {
                ts = [];
                failure = "payload is not MPEG-TS";
                return false;
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            ts = [];
            failure = "unwrap error: " + ex.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Classify from the first bytes of a body (e.g. a 64 KB ranged GET): true when
    /// the PNG carries the TIKTIKPX marker and the start of the payload decodes to
    /// MPEG-TS sync bytes. Safe on truncated input.
    /// </summary>
    public static bool LooksLikeWrappedTs(ReadOnlySpan<byte> prefix)
    {
        try
        {
            if (!IsPng(prefix))
                return false;

            // Enough pixel bytes for header + gzip header + a few TS packets.
            if (!TryDecodePixels(prefix, requireComplete: false, maxPixelBytes: 16 * 1024, out var pixels, out _))
                return false;

            if (!TryReadHeader(pixels, out var payloadLength, out _))
                return false;

            var available = Math.Min((long)payloadLength, pixels.Length - HeaderLength);
            if (available <= 0)
                return false;

            var payload = pixels.AsSpan(HeaderLength, (int)available);
            if (!TryDecodePayload(payload, requireComplete: false, TsPacketSize * 4, out var head, out _))
                return false;

            return head.Length > 0 && LooksLikeTransportStream(head, requireWholePackets: false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the PNG pixel data starts with the TIKTIKPX marker (prefix safe).</summary>
    public static bool HasMarker(ReadOnlySpan<byte> prefix)
    {
        try
        {
            return IsPng(prefix)
                && TryDecodePixels(prefix, requireComplete: false, maxPixelBytes: 4096, out var pixels, out _)
                && pixels.Length >= MarkerLength
                && pixels.AsSpan(0, MarkerLength).SequenceEqual(Marker);
        }
        catch
        {
            return false;
        }
    }

    internal static bool LooksLikeTransportStream(ReadOnlySpan<byte> body, bool requireWholePackets)
    {
        if (body.Length == 0 || body[0] != 0x47)
            return false;
        if (requireWholePackets && body.Length % TsPacketSize != 0)
            return false;

        var checkedPackets = 0;
        for (var i = 0; i < body.Length && checkedPackets < 64; i += TsPacketSize, checkedPackets++)
        {
            if (body[i] != 0x47)
                return false;
        }

        return true;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> pixels, out uint payloadLength, out string failure)
    {
        payloadLength = 0;
        if (pixels.Length < HeaderLength)
        {
            failure = "pixel data shorter than header";
            return false;
        }

        if (!pixels[..MarkerLength].SequenceEqual(Marker))
        {
            failure = "no TIKTIKPX marker (plain image)";
            return false;
        }

        payloadLength = BinaryPrimitives.ReadUInt32BigEndian(pixels.Slice(MarkerLength, 4));
        if (payloadLength == 0 || payloadLength > MaxTsBytes)
        {
            failure = "bad payload length";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryDecodePayload(
        ReadOnlySpan<byte> payload,
        bool requireComplete,
        int maxOutput,
        out byte[] output,
        out string failure)
    {
        output = [];
        if (payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B)
        {
            using var input = new MemoryStream(payload.ToArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            if (!TryReadBounded(gzip, maxOutput, requireComplete, out output, out failure))
                return false;
            return true;
        }

        if (payload.Length > 0 && payload[0] == 0x47)
        {
            // Raw TS directly in the pixels (not seen yet, but harmless).
            output = requireComplete
                ? payload.ToArray()
                : payload[..Math.Min(payload.Length, maxOutput)].ToArray();
            failure = string.Empty;
            return true;
        }

        failure = "payload is neither gzip nor MPEG-TS";
        return false;
    }

    private static bool TryReadBounded(Stream source, int maxOutput, bool requireComplete, out byte[] output, out string failure)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var want = requireComplete ? buffer.Length : Math.Min(buffer.Length, maxOutput - (int)ms.Length);
                if (want <= 0)
                    break;
                var n = source.Read(buffer, 0, want);
                if (n <= 0)
                    break;
                ms.Write(buffer, 0, n);
                if (ms.Length > maxOutput)
                {
                    if (requireComplete)
                    {
                        output = [];
                        failure = "decoded payload too large";
                        return false;
                    }

                    break;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            if (requireComplete)
            {
                output = [];
                failure = "corrupt compressed data";
                return false;
            }
            // Prefix mode: keep what decoded before the truncation.
        }

        output = ms.ToArray();
        failure = string.Empty;
        return output.Length > 0;
    }

    /// <summary>
    /// Minimal PNG decoder: non-interlaced, 8-bit samples, colour types 0/2/3/4/6.
    /// Returns the un-filtered scanline bytes concatenated (no filter-type bytes).
    /// </summary>
    private static bool TryDecodePixels(
        ReadOnlySpan<byte> png,
        bool requireComplete,
        long maxPixelBytes,
        out byte[] pixels,
        out string failure)
    {
        pixels = [];
        if (!IsPng(png))
        {
            failure = "not a PNG";
            return false;
        }

        var pos = PngSignature.Length;
        int width = 0, height = 0, bpp = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var idat = new MemoryStream();

        while (pos + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(pos, 4));
            var type = png.Slice(pos + 4, 4);
            if (length > int.MaxValue)
            {
                failure = "bad chunk length";
                return false;
            }

            var dataStart = pos + 8;
            var available = Math.Min((long)length, png.Length - dataStart);
            var truncated = available < length;
            if (truncated && requireComplete)
            {
                failure = "truncated chunk";
                return false;
            }

            var data = png.Slice(dataStart, (int)Math.Max(0, available));

            if (type.SequenceEqual("IHDR"u8))
            {
                if (data.Length < 13)
                {
                    failure = "short IHDR";
                    return false;
                }

                width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                var depth = data[8];
                var color = data[9];
                if (data[10] != 0 || data[11] != 0 || data[12] != 0)
                {
                    failure = "unsupported compression/filter/interlace";
                    return false;
                }

                bpp = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                if (depth != 8 || bpp == 0 || width <= 0 || height <= 0)
                {
                    failure = "unsupported PNG layout";
                    return false;
                }

                if (width > MaxPixelBytes || height > MaxPixelBytes || (double)width * bpp * height > MaxPixelBytes)
                {
                    failure = "PNG too large";
                    return false;
                }

                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!sawHeader)
                {
                    failure = "IDAT before IHDR";
                    return false;
                }

                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
                break;
            }

            if (truncated)
                break;
            pos = dataStart + (int)length + 4; // + CRC
        }

        if (!sawHeader || idat.Length == 0)
        {
            failure = "no image data";
            return false;
        }

        if (requireComplete && !sawEnd)
        {
            failure = "missing IEND";
            return false;
        }

        var stride = width * bpp;
        var rowBytes = (long)stride + 1;
        var totalRows = (long)height;
        var maxRows = Math.Min(totalRows, Math.Max(1, (maxPixelBytes + stride - 1) / stride));
        var rawWanted = rowBytes * maxRows;

        idat.Position = 0;
        byte[] raw;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            if (!TryReadExactlyUpTo(z, rawWanted, out raw))
            {
                if (requireComplete)
                {
                    failure = "corrupt zlib data";
                    return false;
                }
            }
        }

        var rows = raw.Length / rowBytes;
        if (requireComplete && rows < totalRows)
        {
            failure = "pixel data shorter than IHDR";
            return false;
        }

        if (rows == 0)
        {
            failure = "no complete rows";
            return false;
        }

        pixels = new byte[rows * stride];
        var prev = new byte[stride];
        for (var y = 0; y < rows; y++)
        {
            var src = raw.AsSpan((int)(y * rowBytes), (int)rowBytes);
            var filter = src[0];
            var cur = pixels.AsSpan((int)(y * stride), stride);
            src[1..].CopyTo(cur);
            if (!Unfilter(filter, cur, prev, bpp))
            {
                pixels = [];
                failure = "bad PNG filter type";
                return false;
            }

            cur.CopyTo(prev);
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryReadExactlyUpTo(Stream source, long wanted, out byte[] raw)
    {
        var buffer = new byte[(int)Math.Min(wanted, int.MaxValue)];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var n = source.Read(buffer, total, buffer.Length - total);
                if (n <= 0)
                    break;
                total += n;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            raw = buffer[..total];
            return false;
        }

        raw = total == buffer.Length ? buffer : buffer[..total];
        return true;
    }

    private static bool Unfilter(byte filter, Span<byte> cur, ReadOnlySpan<byte> prev, int bpp)
    {
        switch (filter)
        {
            case 0:
                return true;
            case 1:
                for (var x = bpp; x < cur.Length; x++)
                    cur[x] = (byte)(cur[x] + cur[x - bpp]);
                return true;
            case 2:
                for (var x = 0; x < cur.Length; x++)
                    cur[x] = (byte)(cur[x] + prev[x]);
                return true;
            case 3:
                for (var x = 0; x < cur.Length; x++)
                {
                    var left = x >= bpp ? cur[x - bpp] : 0;
                    cur[x] = (byte)(cur[x] + ((left + prev[x]) >> 1));
                }

                return true;
            case 4:
                for (var x = 0; x < cur.Length; x++)
                {
                    int a = x >= bpp ? cur[x - bpp] : 0;
                    int b = prev[x];
                    int c = x >= bpp ? prev[x - bpp] : 0;
                    cur[x] = (byte)(cur[x] + Paeth(a, b, c));
                }

                return true;
            default:
                return false;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        return pb <= pc ? b : c;
    }
}