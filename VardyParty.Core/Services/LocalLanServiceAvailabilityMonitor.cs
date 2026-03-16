using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace VardyParty.Services;

public sealed class LocalLanServiceAvailabilityMonitor(
    ILocalLanPlayService localLanPlayService,
    ILogger<LocalLanServiceAvailabilityMonitor> logger) : ILocalLanServiceAvailabilityMonitor, IDisposable
{
    private static readonly TimeSpan VerificationInterval = TimeSpan.FromMinutes(1);
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
        await VerifyAndPublishAsync(cancellationToken);

        using var timer = new PeriodicTimer(VerificationInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await VerifyAndPublishAsync(cancellationToken);
        }
    }

    private async Task VerifyAndPublishAsync(CancellationToken cancellationToken)
    {
        try
        {
            var available = await localLanPlayService.IsAvailableAsync(cancellationToken);
            if (available)
            {
                _warningSubject.OnNext(null);
                return;
            }

            _warningSubject.OnNext("Local service unavailable. Ensure VardyParty Local Service is running on your LAN.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LocalLanMonitor] Availability verification failed");
            _warningSubject.OnNext("Unable to verify local service availability right now.");
        }
    }
}