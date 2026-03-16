namespace VardyParty.Services;

public interface ILocalLanServiceAvailabilityMonitor
{
    IObservable<string?> WarningStream { get; }
    void Start();
}