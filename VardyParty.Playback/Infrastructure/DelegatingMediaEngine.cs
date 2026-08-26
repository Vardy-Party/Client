using VardyParty.Kernel;

namespace VardyParty.Playback;

/// <summary>
/// Host-supplied <see cref="IMediaEngine"/>. OS players plug attach/stop/metrics
/// in; they must not put recovery policy here.
/// </summary>
public sealed class DelegatingMediaEngine : IMediaEngine
{
    public event EventHandler<MediaEngineEvent>? EngineEvent;

    public Func<string, IReadOnlyDictionary<string, string>?, CancellationToken, Task>? AttachHandler { get; set; }

    public Func<CancellationToken, Task>? StopHandler { get; set; }

    public Func<PlaybackMetrics?>? MetricsHandler { get; set; }

    public Task AttachAsync(
        string mediaUrl,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        CancellationToken cancellationToken = default)
        => AttachHandler?.Invoke(mediaUrl, requestHeaders, cancellationToken) ?? Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken = default)
        => StopHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;

    public PlaybackMetrics? GetCurrentMetrics() => MetricsHandler?.Invoke();

    public void Raise(MediaEngineEvent engineEvent)
        => EngineEvent?.Invoke(this, engineEvent);
}
