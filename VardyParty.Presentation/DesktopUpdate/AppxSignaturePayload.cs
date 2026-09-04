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
        if (HasFileId(data, "PKCX"u8) || HasFileId(data, "APPX"u8))
        {
            return data[4..];
        }

        return data;
    }

    private static bool HasFileId(byte[] data, ReadOnlySpan<byte> magic) =>
        data.Length > magic.Length && data.AsSpan(0, magic.Length).SequenceEqual(magic);
}
