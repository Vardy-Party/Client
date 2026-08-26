using VardyParty.Kernel;

namespace VardyParty.Streaming;

public class StreamHealthReporter(
    IStreamHealthService streamHealthService,
    ISessionIdProvider sessionIdProvider,
    SelectionState selectionState) : IStreamHealthReporter
{
    public Task ReportPlaybackStartedAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        PlaybackMetrics? metrics = null,
        CancellationToken cancellationToken = default) =>
        ReportAsync("working", streamUrl, refererUrl, streamName, metrics, null, true, cancellationToken);

    public Task ReportBufferingAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        PlaybackMetrics? metrics = null,
        CancellationToken cancellationToken = default) =>
        ReportAsync("buffering", streamUrl, refererUrl, streamName, metrics, null, false, cancellationToken);

    public Task ReportPlaybackErrorAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        string? error = null,
        CancellationToken cancellationToken = default) =>
        ReportAsync("failed", streamUrl, refererUrl, streamName, null, error, false, cancellationToken);

    public Task ReportPlaybackMetricsAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        PlaybackMetrics? metrics = null,
        CancellationToken cancellationToken = default) =>
        ReportAsync("working", streamUrl, refererUrl, streamName, metrics, null, false, cancellationToken);

    public Task ReportBadStreamAsync(
        string? streamUrl,
        string? refererUrl,
        string? streamName = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var userReason = string.IsNullOrWhiteSpace(reason) ? "User reported bad stream" : reason;
        return ReportAsync("user-report", streamUrl, refererUrl, streamName, null, userReason, false,
            cancellationToken);
    }

    private async Task ReportAsync(
        string status,
        string? streamUrl,
        string? refererUrl,
        string? streamName,
        PlaybackMetrics? metrics,
        string? error,
        bool includeMetadata,
        CancellationToken cancellationToken)
    {
        var game = selectionState.CurrentGame;
        if (game == null) return;

        var resolvedStreamUrl = StreamHealthIdentity.ResolveReportUrl(streamUrl, refererUrl);
        if (string.IsNullOrWhiteSpace(resolvedStreamUrl)) return;

        var report = new StreamHealthReport
        {
            StreamUrl = resolvedStreamUrl,
            StreamName = string.IsNullOrWhiteSpace(streamName) ? null : streamName.Trim(),
            Status = status,
            Quality = DetectQuality(metrics),
            Bitrate = metrics?.BitrateKbps,
            Buffering = status == "buffering" || metrics?.IsBuffering == true ? true : null,
            Error = error,
            Resolution = includeMetadata && metrics?.Resolution.HasValue == true
                ? $"{metrics.Resolution.Value.Width}x{metrics.Resolution.Value.Height}"
                : null,
            Framerate = includeMetadata ? metrics?.Framerate : null,
            VideoCodec = includeMetadata ? metrics?.VideoCodec : null,
            AudioCodec = includeMetadata ? metrics?.AudioCodec : null,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionId = sessionIdProvider.SessionId
        };

        await streamHealthService.ReportHealthAsync(
            game.ApiLeague,
            game.Home,
            game.Away,
            report,
            cancellationToken);
    }

    private static string? DetectQuality(PlaybackMetrics? metrics)
    {
        if (metrics?.BitrateKbps == null || metrics?.Resolution == null) return null;

        if (metrics.IsBuffering) return "poor";

        var (_, height) = metrics.Resolution.Value;
        if (metrics.BitrateKbps >= 2000 && height >= 720) return "excellent";
        if (metrics.BitrateKbps >= 1000 || height >= 480) return "good";
        return "poor";
    }
}
