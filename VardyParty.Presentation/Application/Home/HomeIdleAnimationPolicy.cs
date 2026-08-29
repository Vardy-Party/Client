namespace VardyParty.Presentation;

/// <summary>
/// Which decorative animations may run per layout class. This encodes the TV
/// idle invariant: on <see cref="HomeLayoutClass.Tv"/> an IDLE homepage
/// schedules ZERO periodic animation ticks. Field evidence (32-bit TV box,
/// cortex-a9): ~17 infinite live-dot pulses plus the crest's ambient shimmer
/// kept the Choreographer saturated forever — every tick invalidated the view
/// tree and a full pass took ~1.3s on the single weak core, so the queue
/// re-filled faster than it drained.
///
/// The only animations allowed on TV are event-driven and terminating:
/// focus enter/exit (scale + ring fade, ~130ms), the one-shot focus sheen,
/// the resolving pulse (bounded by an in-flight stream resolution), and the
/// crest loading spin (bounded by catalog loading; the settle is one-shot).
/// Nothing may schedule recurring work while the homepage idles.
/// </summary>
public static class HomeIdleAnimationPolicy
{
    /// <summary>
    /// Per-card LIVE-dot pulse (infinite repeat). Denied on TV: live cards get
    /// a static LIVE treatment (solid dot + red chip) instead of 17 competing
    /// animation clocks.
    /// </summary>
    public static bool AllowLiveDotPulse(HomeLayoutClass layoutClass) =>
        layoutClass != HomeLayoutClass.Tv;

    /// <summary>
    /// The crest's ambient sheen loop (infinite repeat, re-armed after every
    /// settle). Denied on TV: the crest may only sheen on focus change.
    /// </summary>
    public static bool AllowAmbientCrestShimmer(HomeLayoutClass layoutClass) =>
        layoutClass != HomeLayoutClass.Tv;
}
