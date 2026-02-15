namespace VardyParty.Providers;

public class SessionIdProvider : ISessionIdProvider
{
    public string SessionId { get; } = Guid.NewGuid().ToString();
}
