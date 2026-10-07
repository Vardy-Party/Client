namespace VardyParty.Streaming;

/// <summary>
/// A remote-compute failure the signed-in user can read: which part stopped,
/// why, and the correlation id an admin can look up.
/// </summary>
public sealed record RemoteComputeFault(string Component, string Message, string CorrelationId)
{
    public string Display =>
        $"{Component} failed: {Message}. Correlation id: {CorrelationId}";
}
