using System.Buffers.Binary;

namespace VardyParty.Streaming;

/// <summary>
/// Second TikTok-CDN wrapper (seen on castppv / uzhi.ru <c>tntsports1-uk</c>, Oct 2026):
/// a RIFF/WebP file (<c>Content-Type: image/webp</c>) holding a tiny real image
/// chunk (1x1 <c>VP8L</c>) followed by an <c>EXIF</c> chunk whose payload is raw
/// MPEG-TS (no compression, no header). The TS is returned as-is.
/// Never throws; plain WebP images (no TS-carrying chunk) are rejected.
/// </summary>
public static class WebpWrappedTs
{
    /// <summary>Largest RIFF body unwrapped (same bound as PNG).</summary>
    public const int MaxWebpBytes = PngWrappedTs.MaxPngBytes;

    private const int RiffHeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const int MinTsPackets = 2;

    public static bool IsWebp(ReadOnlySpan<byte> body) =>
        body.Length >= RiffHeaderLength
        && body[..4].SequenceEqual("RIFF"u8)
        && body.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>Prefix check (e.g. a 64 KB ranged probe): a WebP whose EXIF/XMP chunk starts with MPEG-TS packets.</summary>
    public static bool LooksLikeWrappedTs(ReadOnlySpan<byte> prefix) =>
        TryFindTsChunk(prefix, requireComplete: false, out var start, out var length)
        && LooksLikeTs(prefix.Slice(start, Math.Min(length, prefix.Length - start)));

    public static bool TryUnwrap(ReadOnlySpan<byte> webp, out byte[] ts) =>
        TryUnwrap(webp, out ts, out _);

    public static bool TryUnwrap(ReadOnlySpan<byte> webp, out byte[] ts, out string failure)
    {
        ts = [];
        try
        {
            if (webp.Length > MaxWebpBytes)
            {
                failure = "webp larger than cap";
                return false;
            }

            if (!TryFindTsChunk(webp, requireComplete: true, out var start, out var length))
            {
                failure = "no complete TS-carrying chunk (plain image)";
                return false;
            }

            var payload = webp.Slice(start, length);
            if (payload.Length > PngWrappedTs.MaxTsBytes || !LooksLikeTs(payload))
            {
                failure = "chunk payload is not MPEG-TS";
                return false;
            }

            ts = payload.ToArray();
            failure = "";
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = "malformed webp: " + ex.GetType().Name;
            ts = [];
            return false;
        }
    }

    private static bool LooksLikeTs(ReadOnlySpan<byte> payload) =>
        payload.Length >= MinTsPackets * PngWrappedTs.TsPacketSize
        && PngWrappedTs.LooksLikeTransportStream(payload, requireWholePackets: false);

    /// <summary>Walk RIFF chunks; return the first metadata chunk (EXIF/XMP/unknown) starting with 0x47.</summary>
    private static bool TryFindTsChunk(ReadOnlySpan<byte> body, bool requireComplete, out int start, out int length)
    {
        start = 0;
        length = 0;
        if (!IsWebp(body))
            return false;

        var riffEnd = (long)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)) + 8;
        if (requireComplete && riffEnd > body.Length)
            return false;

        long pos = RiffHeaderLength;
        for (var guard = 0; guard < 64 && pos + ChunkHeaderLength <= Math.Min(riffEnd, body.Length); guard++)
        {
            var p = (int)pos;
            var fourCc = body.Slice(p, 4);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(p + 4, 4));
            var dataStart = pos + ChunkHeaderLength;
            if (dataStart + size > riffEnd)
                return false;

            var isImageData = fourCc.SequenceEqual("VP8 "u8) || fourCc.SequenceEqual("VP8L"u8)
                || fourCc.SequenceEqual("VP8X"u8) || fourCc.SequenceEqual("ALPH"u8)
                || fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8)
                || fourCc.SequenceEqual("ICCP"u8);
            if (!isImageData && size > 0 && dataStart < body.Length && body[(int)dataStart] == 0x47)
            {
                if (requireComplete && dataStart + size > body.Length)
                    return false;
                start = (int)dataStart;
                length = (int)Math.Min(size, int.MaxValue);
                return true;
            }

            pos = dataStart + size + (size & 1);
        }

        return false;
    }
}