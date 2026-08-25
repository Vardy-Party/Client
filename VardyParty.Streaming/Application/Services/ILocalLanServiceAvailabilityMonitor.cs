namespace VardyParty.Streaming;

public interface ILocalLanServiceAvailabilityMonitor
{
    IObservable<string?> WarningStream { get; }
    void Start();
}