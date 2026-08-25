namespace VardyParty.Streaming;

public class SessionIdProvider : ISessionIdProvider
{
    public string SessionId { get; } = Guid.NewGuid().ToString();
}
