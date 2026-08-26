using VardyParty.Kernel;

namespace VardyParty.Playback;

/// <summary>
/// Slim OS media adapter. Implementations should only drive the native decoder/UI chrome
/// and forward engine facts — no switch/recover/health policy.
/// </summary>
public interface IMediaEngine
{
    /// <summary>Raised for Ready / Buffering / Error / Ended / Metrics (generation-scoped).</summary>
    event EventHandler<MediaEngineEvent>? EngineEvent;

    /// <summary>Attach an HLS/M3U8 (or equivalent) source. Must not decide recovery policy.</summary>
    Task AttachAsync(
        string mediaUrl,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stop and release the current source without closing the whole app session.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Current decoder metrics, if available.</summary>
    PlaybackMetrics? GetCurrentMetrics();
}
