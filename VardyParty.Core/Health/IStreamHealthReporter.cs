using VardyParty.Models;

namespace VardyParty.Services;

public interface IStreamHealthReporter
{
    Task ReportPlaybackStartedAsync(string? streamUrl, string? refererUrl, PlaybackMetrics? metrics = null, CancellationToken cancellationToken = default);
    Task ReportBufferingAsync(string? streamUrl, string? refererUrl, PlaybackMetrics? metrics = null, CancellationToken cancellationToken = default);
    Task ReportPlaybackErrorAsync(string? streamUrl, string? refererUrl, string? error, CancellationToken cancellationToken = default);
    Task ReportPlaybackMetricsAsync(string? streamUrl, string? refererUrl, PlaybackMetrics? metrics = null, CancellationToken cancellationToken = default);
}