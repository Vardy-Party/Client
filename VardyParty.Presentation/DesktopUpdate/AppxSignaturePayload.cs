namespace VardyParty.Presentation;

/// <summary>
/// <c>AppxSignature.p7x</c> is PKCS#7 SignedData with a 4-byte SIP file id.
/// Current SignTool writes <c>PKCX</c>; older packages used <c>APPX</c>.
/// </summary>
public static class AppxSignaturePayload
{
    public static byte[] Unwrap(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (TryStripFileId(data, "PKCX"u8, out var stripped) ||
            TryStripFileId(data, "APPX"u8, out stripped))
        {
            return stripped;
        }

        return data;
    }

    private static bool TryStripFileId(byte[] data, ReadOnlySpan<byte> magic, out byte[] stripped)
    {
        if (data.Length > magic.Length && data.AsSpan(0, magic.Length).SequenceEqual(magic))
        {
            stripped = data[magic.Length..];
            return true;
        }

        stripped = null!;
        return false;
    }
}
