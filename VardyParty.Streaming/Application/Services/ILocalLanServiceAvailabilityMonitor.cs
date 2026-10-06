namespace VardyParty.Streaming;

public interface ILocalLanServiceAvailabilityMonitor
{
    IObservable<string?> WarningStream { get; }

    /// <summary>
    /// True only after discovery has confirmed a LAN local-service that can
    /// host-share. Starts false so share stays off until one is found.
    /// Guest / phone remote-compute pairing does not count as found.
    /// </summary>
    IObservable<bool> FoundStream { get; }

    void Start();
}