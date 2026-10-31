using VardyParty.Kernel;

namespace VardyParty.Streaming;

public class StreamHealthReporter(
    IStreamHealthService streamHealthService,
    ISessionIdProvider sessionIdProvider,
    SelectionState selectionState) : IStreamHealthReporter
{
    /// <summary>
    /// The orchestrator's 30s tick and the native player's 30s timer both report
    /// the stream that is playing. Without this window the API got every
    /// working sample twice (seen at 16:49:27 and 16:49:29 UK time on 31 Oct 2026).
    /// </summary>
    internal static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A failure is reported once per stream. The native session and the
    /// orchestrator fallback can both report the same drop.
    /// </summary>
    internal static readonly TimeSpan DuplicateFailureWindow = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Test seam for the duplicate windows.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.UtcNow;

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

        // A started (metadata) report has its own key, so a metrics sample never
        // swallows it. The orchestrator and the native player both send one
        // per play, and only the first in the window is kept. user-report is
        // the user's explicit action and is never dropped.
        if (status != "user-report"
            && IsDuplicate(includeMetadata ? "started" : status, resolvedStreamUrl, streamName))
            return;

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

    private bool IsDuplicate(string status, string url, string? streamName)
    {
        var key = $"{status}|{StreamHealthIdentity.BuildStreamKey(url, streamName)}";
        var window = status == "failed" ? DuplicateFailureWindow : DuplicateWindow;
        var now = Clock();
        lock (_gate)
        {
            if (_lastSent.TryGetValue(key, out var last) && now - last < window)
                return true;

            _lastSent[key] = now;
            if (_lastSent.Count > 256)
            {
                foreach (var stale in _lastSent.Where(p => now - p.Value >= DuplicateFailureWindow).Select(p => p.Key).ToList())
                    _lastSent.Remove(stale);
            }

            return false;
        }
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
