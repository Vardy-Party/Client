using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace VardyParty.Streaming;

public sealed class LocalLanServiceAvailabilityMonitor(
    ILocalLanPlayService localLanPlayService,
    ILogger<LocalLanServiceAvailabilityMonitor> logger) : ILocalLanServiceAvailabilityMonitor, IDisposable
{
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UnavailableFastInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnavailableNormalInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnavailableFastWindow = TimeSpan.FromMinutes(1);
    private readonly BehaviorSubject<string?> _warningSubject = new(null);
    private readonly BehaviorSubject<bool> _foundSubject = new(false);
    private CancellationTokenSource? _cts;
    private int _started;

    public IObservable<string?> WarningStream => _warningSubject.AsObservable();

    public IObservable<bool> FoundStream => _foundSubject.AsObservable();

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
        _foundSubject.Dispose();
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
            var capable = available && await localLanPlayService.SupportsComputeHostAsync(cancellationToken);
            _foundSubject.OnNext(capable);

            if (localLanPlayService.UsesRemoteCompute)
            {
                _warningSubject.OnNext(null);
                return true;
            }

            if (available)
            {
                _warningSubject.OnNext(null);
                return true;
            }

            var warning = localLanPlayService.LastHealthStatus switch
            {
                HttpStatusCode.Unauthorized =>
                    "Local service requires authentication. Sign in to enable local stream playback.",
                HttpStatusCode.Forbidden =>
                    "Local service access denied. Stream viewer permission required.",
                _ =>
                    "Local service unavailable. Ensure VardyParty Local Service is running on your LAN."
            };

            _warningSubject.OnNext(warning);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanMonitor] Availability verification failed");
            _foundSubject.OnNext(false);
            _warningSubject.OnNext("Unable to verify local service availability right now.");
            return false;
        }
    }
}