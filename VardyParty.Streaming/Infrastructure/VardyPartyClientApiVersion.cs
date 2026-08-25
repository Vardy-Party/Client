namespace VardyParty.Streaming;

/// <summary>
/// API version negotiation via <c>X-VardyParty-Client-Api-Version</c>.
/// </summary>
public static class VardyPartyClientApiVersion
{
    public const string HeaderName = "X-VardyParty-Client-Api-Version";

    public const int Legacy = 1;
    public const int V2StreamMetadata = 2;

    public const string DefaultHeaderValue = "2";
}
