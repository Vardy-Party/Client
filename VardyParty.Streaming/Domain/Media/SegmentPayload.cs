namespace VardyParty.Streaming;

/// <summary>What an HLS media segment body actually is, judged from its bytes.</summary>
public enum SegmentPayloadKind
{
    /// <summary>Not enough bytes or an unrecognised format.</summary>
    Unknown = 0,
    MpegTs,
    Fmp4,
    /// <summary>
    /// MPEG-TS hidden in an image container on TikTok CDNs: PNG pixels (<c>TIKTIKPX</c>,
    /// <see cref="PngWrappedTs"/>) or a WebP EXIF chunk (<see cref="WebpWrappedTs"/>). Playable after unwrap.
    /// </summary>
    WrappedTs,
    /// <summary>A real image (PNG without the marker, JPEG, GIF, WebP): decoy / placeholder.</summary>
    Image,
    /// <summary>HTML / JSON / text error page.</summary>
    Text
}

/// <summary>
/// Byte-level segment classification shared by health checks and every player hook.
/// </summary>
public static class SegmentPayload
{
    /// <summary>Bytes a health probe should read to classify a segment (ranged GET).</summary>
    public const int ProbeBytes = 64 * 1024;

    /// <summary>Content-Type players should see for an unwrapped segment.</summary>
    public const string MpegTsContentType = "video/mp2t";

    public static SegmentPayloadKind Classify(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 4)
            return SegmentPayloadKind.Unknown;

        if (PngWrappedTs.IsPng(prefix))
        {
            return PngWrappedTs.LooksLikeWrappedTs(prefix)
                ? SegmentPayloadKind.WrappedTs
                : SegmentPayloadKind.Image;
        }

        if (WebpWrappedTs.IsWebp(prefix))
        {
            return WebpWrappedTs.LooksLikeWrappedTs(prefix)
                ? SegmentPayloadKind.WrappedTs
                : SegmentPayloadKind.Image;
        }

        // Before TS: "GIF8" starts with 0x47, the TS sync byte.
        if (LooksLikeOtherImage(prefix))
            return SegmentPayloadKind.Image;

        if (LooksLikeMpegTs(prefix))
            return SegmentPayloadKind.MpegTs;

        if (LooksLikeFmp4(prefix))
            return SegmentPayloadKind.Fmp4;

        if (LooksLikeText(prefix))
            return SegmentPayloadKind.Text;

        return SegmentPayloadKind.Unknown;
    }

    /// <summary>Playable on every client platform (players route WrappedTs through the unwrapper).</summary>
    public static bool IsPlayable(SegmentPayloadKind kind) =>
        kind is SegmentPayloadKind.MpegTs or SegmentPayloadKind.Fmp4 or SegmentPayloadKind.WrappedTs;

    /// <summary>
    /// Player-side normalisation of a fully downloaded segment: PNG-wrapped TS is
    /// replaced by its MPEG-TS bytes; everything else is returned unchanged.
    /// Never throws.
    /// </summary>
    public static byte[] NormalizeForPlayback(byte[] body, out bool unwrapped)
    {
        unwrapped = false;
        if (body is null || !IsWrapperContainer(body))
            return body ?? [];

        if (TryUnwrap(body, out var ts))
        {
            unwrapped = true;
            return ts;
        }

        return body;
    }

    /// <summary>Image containers that may carry wrapped TS (PNG or WebP); players buffer only these.</summary>
    public static bool IsWrapperContainer(ReadOnlySpan<byte> body) =>
        PngWrappedTs.IsPng(body) || WebpWrappedTs.IsWebp(body);

    /// <summary>Unwrap either wrapper format. Never throws; false for plain images.</summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> body, out byte[] ts)
    {
        if (PngWrappedTs.IsPng(body))
            return PngWrappedTs.TryUnwrap(body, out ts);
        if (WebpWrappedTs.IsWebp(body))
            return WebpWrappedTs.TryUnwrap(body, out ts);
        ts = [];
        return false;
    }

    private static bool LooksLikeMpegTs(ReadOnlySpan<byte> body)
    {
        if (body[0] != 0x47)
            return false;
        // A single sync byte is weak; require the next packet when we have it.
        if (body.Length > PngWrappedTs.TsPacketSize)
            return PngWrappedTs.LooksLikeTransportStream(body, requireWholePackets: false);
        return true;
    }

    private static readonly string[] Fmp4LeadingBoxTypes =
        ["ftyp", "styp", "moof", "moov", "sidx", "prft", "emsg"];

    private static bool LooksLikeFmp4(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8)
            return false;

        var size = ((uint)body[0] << 24) | ((uint)body[1] << 16) | ((uint)body[2] << 8) | body[3];
        if (size is > 1 and < 8)
            return false;

        foreach (var type in Fmp4LeadingBoxTypes)
        {
            if (body[4] == type[0] && body[5] == type[1] && body[6] == type[2] && body[7] == type[3])
                return true;
        }

        return false;
    }

    private static bool LooksLikeOtherImage(ReadOnlySpan<byte> body) =>
        (body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF)
        || (body[0] == (byte)'G' && body[1] == (byte)'I' && body[2] == (byte)'F' && body[3] == (byte)'8');

    private static bool LooksLikeText(ReadOnlySpan<byte> body)
    {
        var i = 0;
        while (i < body.Length && i < 16 && body[i] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0xEF or 0xBB or 0xBF)
            i++;
        return i < body.Length && body[i] is (byte)'<' or (byte)'{' or (byte)'[' or (byte)'#';
    }
}