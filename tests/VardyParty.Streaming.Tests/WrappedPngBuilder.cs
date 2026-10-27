using System;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;

namespace VardyParty.Streaming.Tests;

/// <summary>Test-only PNG writer that reproduces the TikTok CDN TIKTIKPX wrapper.</summary>
internal static class WrappedPngBuilder
{
    public static byte[] Fixture(string name)
    {
        var asm = typeof(WrappedPngBuilder).Assembly;
        var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>MPEG-TS-looking bytes: <paramref name="packets"/> packets with 0x47 sync.</summary>
    public static byte[] FakeTs(int packets, int seed = 7)
    {
        var rng = new Random(seed);
        var ts = new byte[packets * 188];
        rng.NextBytes(ts);
        for (var i = 0; i < ts.Length; i += 188)
            ts[i] = 0x47;
        return ts;
    }

    /// <summary>gzip with stored (no-compression) deflate, as in the captured segments.</summary>
    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.NoCompression, leaveOpen: true))
            gz.Write(data);
        return ms.ToArray();
    }

    /// <summary>TIKTIKPX + BE length + payload, padded with random bytes.</summary>
    public static byte[] WrapperPixels(byte[] payload, string marker = "TIKTIKPX", int padding = 575, int? declaredLength = null)
    {
        var pixels = new byte[12 + payload.Length + padding];
        System.Text.Encoding.ASCII.GetBytes(marker).AsSpan(0, Math.Min(8, marker.Length)).CopyTo(pixels);
        BinaryPrimitives.WriteUInt32BigEndian(pixels.AsSpan(8, 4), (uint)(declaredLength ?? payload.Length));
        payload.CopyTo(pixels, 12);
        new Random(11).NextBytes(pixels.AsSpan(12 + payload.Length));
        return pixels;
    }

    public static byte[] Wrap(byte[] ts, int width = 512, byte colorType = 2, Func<int, byte>? filterForRow = null) =>
        Png(WrapperPixels(Gzip(ts)), width, colorType, filterForRow);

    /// <summary>Encode raw sample bytes as an 8-bit PNG; the last row is zero-padded.</summary>
    public static byte[] Png(
        byte[] samples,
        int width,
        byte colorType = 2,
        Func<int, byte>? filterForRow = null,
        byte depth = 8,
        byte interlace = 0,
        int idatChunkSize = 8192,
        bool includeEnd = true)
    {
        var bpp = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 3 };
        var stride = width * bpp;
        var height = Math.Max(1, (samples.Length + stride - 1) / stride);
        var rows = new byte[height * stride];
        samples.CopyTo(rows, 0);
        filterForRow ??= y => (byte)(y % 5);

        using var raw = new MemoryStream();
        var prev = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var cur = rows.AsSpan(y * stride, stride);
            var filter = filterForRow(y);
            raw.WriteByte(filter);
            var line = new byte[stride];
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? cur[x - bpp] : 0;
                int b = prev[x];
                int c = x >= bpp ? prev[x - bpp] : 0;
                int predictor = filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => 0
                };
                line[x] = (byte)(cur[x] - predictor);
            }

            raw.Write(line);
            cur.CopyTo(prev);
        }

        byte[] zlib;
        using (var zms = new MemoryStream())
        {
            using (var z = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw.ToArray());
            zlib = zms.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = depth;
        ihdr[9] = colorType;
        ihdr[12] = interlace;
        Chunk(png, "IHDR", ihdr);
        for (var i = 0; i < zlib.Length; i += idatChunkSize)
            Chunk(png, "IDAT", zlib.AsSpan(i, Math.Min(idatChunkSize, zlib.Length - i)));
        if (includeEnd)
            Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// RIFF/WebP like the castppv/uzhi.ru capture: a tiny VP8L image chunk, optional
    /// extra chunks, then <paramref name="carrierFourCc"/> (EXIF) holding raw TS.
    /// </summary>
    public static byte[] Webp(byte[]? exifPayload, string carrierFourCc = "EXIF", int? riffSizeOverride = null, params (string FourCc, byte[] Data)[] extraBefore)
    {
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        RiffChunk(body, "VP8L", [0x2F, 0, 0, 0, 0x10, 0x07, 0x10, 0x11, 0x11, 0x88, 0x88, 0xFE, 0x07]);
        foreach (var (fourCc, data) in extraBefore)
            RiffChunk(body, fourCc, data);
        if (exifPayload is not null)
            RiffChunk(body, carrierFourCc, exifPayload);
        var inner = body.ToArray();
        using var riff = new MemoryStream();
        riff.Write("RIFF"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(riffSizeOverride ?? inner.Length));
        riff.Write(size);
        riff.Write(inner);
        return riff.ToArray();
    }

    private static void RiffChunk(Stream s, string fourCc, ReadOnlySpan<byte> data)
    {
        s.Write(System.Text.Encoding.ASCII.GetBytes(fourCc));
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(data);
        if ((data.Length & 1) == 1)
            s.WriteByte(0);
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        s.Write(System.Text.Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write([0, 0, 0, 0]); // CRC not checked by the decoder
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