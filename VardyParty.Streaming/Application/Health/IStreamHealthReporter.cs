using VardyParty.Kernel;

namespace VardyParty.Streaming;

public interface IStreamHealthReporter
{
    Task ReportPlaybackStartedAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        PlaybackMetrics? metrics = null,
        CancellationToken cancellationToken = default);

    Task ReportBufferingAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        PlaybackMetrics? metrics = null,
        CancellationToken cancellationToken = default);

    Task ReportPlaybackErrorAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        string? error = null,
        CancellationToken cancellationToken = default);

    Task ReportPlaybackMetricsAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        PlaybackMetrics? metrics = null,
        CancellationToken cancellationToken = default);

    Task ReportBadStreamAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        string? reason = null,
        CancellationToken cancellationToken = default);
}
