namespace VardyParty.Presentation;

/// <summary>
/// In-process pub/sub for DELIVERED match events (the ones that passed
/// <see cref="MatchEventNotificationPolicy.ShouldPresent"/> — visibility-
/// filtered, foreground-gated, toggle-gated). The homepage toast is today's
/// only consumer; the planned playback overlays (toasts rendered over the
/// native video players, next dispatch) subscribe to the SAME stream instead
/// of growing their own detector. Publish and subscription callbacks run on
/// the UI thread (the catalog apply pump).
/// </summary>
public sealed class MatchEventBus
{
    /// <summary>Raised once per delivered event, on the UI thread.</summary>
    public event Action<MatchEvent>? Published;

    public void Publish(MatchEvent matchEvent) => Published?.Invoke(matchEvent);
}
