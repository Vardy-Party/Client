namespace VardyParty.Kernel;

/// <summary>
/// Cross-platform first-picture + early-Next LocalService capacity rules.
/// Every host (Android, Windows, desktop) must go through this policy — do not
/// fork open/Next behaviour per OS. Tune here while experimenting; orchestrator
/// and <c>PlaybackUrlResolver</c> only execute the plan.
/// </summary>
/// <remarks>
/// Measured stall (Bravia 3.3.15): dual startup <c>/play</c> race held finding UI
/// ~25s after two healthy rows. This strategy opens the recommended row alone and
/// warms the partner for seamless Next, while pausing find-phase Chromium.
/// </remarks>
public static class FirstPlayOpenStrategy
{
    /// <summary>
    /// How long find-phase stays paused after first playback starts so Next can
    /// take a warm URL (unless <see cref="PlaybackUrlReusePolicy.PrewarmDepth"/>
    /// is satisfied sooner).
    /// </summary>
    public static readonly TimeSpan PlaybackPriorityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What the orchestrator should do to open the first picture.</summary>
    public enum StartKind
    {
        /// <summary>Row already reusable or warm/in-flight — PlayStream uses existing path.</summary>
        UseExisting,

        /// <summary>
        /// Fresh <c>/play</c> the recommended row only; open as soon as it is playable.
        /// Optionally warm <see cref="StartPlan.PartnerToWarm"/> on the second slot
        /// without waiting for picture.
        /// </summary>
        ResolveRecommendedOpenFirst,
    }

    /// <summary>Plan for the first playback-chain start.</summary>
    public readonly record struct StartPlan(
        StartKind Kind,
        EnrichedStream? PartnerToWarm,
        string Reason);

    /// <summary>What to do after the recommended open-first leg classifies.</summary>
    public enum AfterRecommendedKind
    {
        /// <summary>Apply URL and open the player on the recommended row.</summary>
        OpenRecommended,

        /// <summary>
        /// Recommended is empty/audio-only/infrastructure — try a cached pool row,
        /// else await the partner warm leg.
        /// </summary>
        AwaitPartnerOrCached,

        /// <summary>No playable URL from this start.</summary>
        Fail,
    }

    /// <summary>How Next should prepare the target row before the platform attaches.</summary>
    public enum NextPrepareKind
    {
        /// <summary>Keep ResolvedM3U8Url / UnusedResolvedUrl; PlanSwitch AttachCached.</summary>
        AttachAsIs,

        /// <summary>
        /// Warm cache has a ready entry — apply onto the row as unused so every
        /// platform's PlanSwitch path attaches without a second Chromium <c>/play</c>.
        /// </summary>
        ApplyWarmUnused,

        /// <summary>
        /// Clear burned find URL and let the player ResolveFresh (joins warm in-flight
        /// via TryTake-before-wait).
        /// </summary>
        ClearAndResolveFresh,
    }

    /// <summary>
    /// Decide how to open the first picture. Prefer open-recommended; never block
    /// picture on a dual-leg winner wait.
    /// </summary>
    public static StartPlan PlanStart(
        EnrichedStream recommended,
        IReadOnlyList<EnrichedStream> healthy,
        DateTimeOffset now,
        Func<EnrichedStream, bool>? isWarmOrInFlight = null)
    {
        ArgumentNullException.ThrowIfNull(recommended);
        ArgumentNullException.ThrowIfNull(healthy);

        if (!PlaybackRacePolicy.NeedsFreshPlay(recommended, now))
        {
            return new StartPlan(
                StartKind.UseExisting,
                PartnerToWarm: null,
                Reason: "recommended can play without fresh /play");
        }

        if (isWarmOrInFlight?.Invoke(recommended) == true)
        {
            return new StartPlan(
                StartKind.UseExisting,
                PartnerToWarm: null,
                Reason: "recommended already warm or pre-resolving");
        }

        var partner = PlaybackRacePolicy.PickPartner(
            healthy, recommended, now, isWarmOrInFlight);

        return new StartPlan(
            StartKind.ResolveRecommendedOpenFirst,
            PartnerToWarm: partner,
            Reason: partner?.Stream is not null
                ? $"open recommended first; warm partner {partner.Stream.Channel} for Next"
                : "open recommended first; no partner to warm");
    }

    /// <summary>
    /// After the recommended fresh leg finishes classifying, whether to open it
    /// or fall back (cached row / partner warm).
    /// </summary>
    public static AfterRecommendedKind DecideAfterRecommended(RaceLegState recommended) =>
        recommended switch
        {
            RaceLegState.Video or RaceLegState.Unverified => AfterRecommendedKind.OpenRecommended,
            RaceLegState.Empty or RaceLegState.AudioOnly or RaceLegState.Infrastructure
                => AfterRecommendedKind.AwaitPartnerOrCached,
            _ => AfterRecommendedKind.Fail,
        };

    /// <summary>
    /// True when a race/partner leg URL may be kept warm for one later Next.
    /// </summary>
    public static bool ShouldWarmPartnerLeg(RaceLegState state) =>
        state is RaceLegState.Video or RaceLegState.Unverified;

    /// <summary>
    /// True when the recommended (or partner) leg is a stream verdict to report/remove.
    /// </summary>
    public static bool IsStreamVerdict(RaceLegState state) =>
        state is RaceLegState.Empty or RaceLegState.AudioOnly;

    /// <summary>
    /// Prepare the Next target so every platform attaches the same way
    /// (Android NativeVideo, Windows pool commands, etc.).
    /// </summary>
    /// <param name="warmEntryReady">
    /// True when the warm cache has a takeable entry for this row right now
    /// (Peek with comfortable life), not merely in-flight.
    /// </param>
    public static NextPrepareKind PlanNextPrepare(
        EnrichedStream next,
        DateTimeOffset now,
        bool warmEntryReady)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (next.UnusedResolvedUrl && !string.IsNullOrWhiteSpace(next.ResolvedM3U8Url))
            return NextPrepareKind.AttachAsIs;

        if (PlaybackUrlReusePolicy.CanReuseCached(next, now))
            return NextPrepareKind.AttachAsIs;

        if (warmEntryReady)
            return NextPrepareKind.ApplyWarmUnused;

        if (LocalServiceSoakTrustRequiresFresh(next, now))
            return NextPrepareKind.ClearAndResolveFresh;

        if (string.IsNullOrWhiteSpace(next.ResolvedM3U8Url))
            return NextPrepareKind.ClearAndResolveFresh;

        return NextPrepareKind.AttachAsIs;
    }

    /// <summary>
    /// Playback-priority window is done when enough upcoming rows are warm
    /// (or the timeout elapses in the host).
    /// </summary>
    public static bool PlaybackPrioritySatisfied(int warmUpcomingCount) =>
        warmUpcomingCount >= PlaybackUrlReusePolicy.PrewarmDepth;

    /// <summary>
    /// Count upcoming switch-order rows that already have a warm entry with
    /// comfortable life left (host supplies Peek expiry via callback).
    /// </summary>
    public static int CountWarmUpcoming(
        IReadOnlyList<EnrichedStream> healthy,
        EnrichedStream? current,
        DateTimeOffset now,
        Func<EnrichedStream, DateTimeOffset?> warmExpiry)
    {
        ArgumentNullException.ThrowIfNull(healthy);
        ArgumentNullException.ThrowIfNull(warmExpiry);

        var upcoming = PlaybackUrlReusePolicy.UpcomingInSwitchOrder(
            healthy, current, PlaybackUrlReusePolicy.PrewarmDepth);
        var n = 0;
        foreach (var row in upcoming)
        {
            var expiry = warmExpiry(row);
            if (expiry is { } e && e - now > PlaybackUrlReusePolicy.MinComfortableLife)
                n++;
        }

        return n;
    }

    /// <summary>
    /// Soak/one-shot that still needs a fresh resolve for attach (mirrors orchestrator
    /// RequiresFresh &amp;&amp; !CanReuseCached without taking a Streaming dependency).
    /// </summary>
    private static bool LocalServiceSoakTrustRequiresFresh(EnrichedStream row, DateTimeOffset now)
    {
        if (row.Stream is null)
            return false;
        return PlaybackUrlReusePolicy.RequiresFreshM3u8ForPlayback(row)
            && !PlaybackUrlReusePolicy.CanReuseCached(row, now);
    }
}
