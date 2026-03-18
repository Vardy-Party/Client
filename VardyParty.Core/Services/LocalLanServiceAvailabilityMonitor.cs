using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace VardyParty.Services;

public sealed class LocalLanServiceAvailabilityMonitor(
    ILocalLanPlayService localLanPlayService,
    ILogger<LocalLanServiceAvailabilityMonitor> logger) : ILocalLanServiceAvailabilityMonitor, IDisposable
{
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UnavailableFastInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnavailableNormalInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnavailableFastWindow = TimeSpan.FromMinutes(1);
    private readonly BehaviorSubject<string?> _warningSubject = new(null);
    private CancellationTokenSource? _cts;
    private int _started;

    public IObservable<string?> WarningStream => _warningSubject.AsObservable();

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _warningSubject.Dispose();
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? unavailableSince = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var isAvailable = await VerifyAndPublishAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;

            if (isAvailable)
            {
                unavailableSince = null;
            }
            else
            {
                unavailableSince ??= now;
            }

            var nextDelay = GetNextDelay(isAvailable, unavailableSince, now);
            await Task.Delay(nextDelay, cancellationToken);
        }
    }

    private static TimeSpan GetNextDelay(bool isAvailable, DateTimeOffset? unavailableSince, DateTimeOffset now)
    {
        if (isAvailable || unavailableSince == null)
            return VerificationInterval;

        var unavailableFor = now - unavailableSince.Value;
        return unavailableFor < UnavailableFastWindow ? UnavailableFastInterval : UnavailableNormalInterval;
    }

    private async Task<bool> VerifyAndPublishAsync(CancellationToken cancellationToken)
    {
        try
        {
            var available = await localLanPlayService.IsAvailableAsync(cancellationToken);
            if (available)
            {
                _warningSubject.OnNext(null);
                return true;
            }

            _warningSubject.OnNext("Local service unavailable. Ensure VardyParty Local Service is running on your LAN.");
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanMonitor] Availability verification failed");
            _warningSubject.OnNext("Unable to verify local service availability right now.");
            return false;
        }
    }
}