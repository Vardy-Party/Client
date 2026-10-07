namespace VardyParty.Ports;

/// <summary>
/// Keeps the process alive while a relayed resolve is in flight.
/// Android starts a foreground service; other heads do nothing.
/// </summary>
public interface IRemoteComputeKeepAlive
{
    void Start();

    void Stop();
}

public sealed class NoopRemoteComputeKeepAlive : IRemoteComputeKeepAlive
{
    public void Start()
    {
    }

    public void Stop()
    {
    }
}
