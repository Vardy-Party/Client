namespace VardyParty.Ports;

/// <summary>
/// Tells the discovered M3U8 resolver that the DoH preference changed so it can
/// close the long-lived browser and launch the next one to match.
/// </summary>
public interface ILocalLanDnsNotifier
{
    Task NotifyAsync(bool enabled, CancellationToken cancellationToken = default);
}
