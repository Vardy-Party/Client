namespace VardyParty.Kernel;

/// <summary>
/// Cross-platform first-picture + early-Next LocalService capacity rules.
/// Every host (Android, Windows, desktop) must go through this policy — do not
/// fork open/Next behaviour per OS. Tune here while experimenting; orchestrator
/// and <c>PlaybackUrlResolver</c> only execute the plan.
/// </summary>
/// <remarks>
/// Measured stall (Bravia 3.3.15): dual startup <c>/play</c> race held finding UI
/// ~25s after two healthy rows. This strategy opens the start row alone, on its
/// probed link when that can be reused (docs/PROBE_SCHEDULING_RULES.md rule 12), and
/// warms the backup for seamless Next.
/// </remarks>
public static class FirstPlayOpenStrategy
{
    /// <summary>
    /// How long after first playback starts the priority window may pause find-phase,
    /// and only while a backup needs warming (<see cref="PlaybackPriorityHolds"/>).
    /// </summary>
    public static readonly TimeSpan PlaybackPriorityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What the orchestrator should do to open the first picture.</summary>
    public enum StartKind
    {
        /// <summary>Row already reusable or warm/in-flight — PlayStream uses existing path.</summary>
        UseExisting,

        /// <summary>
        /// Play the link the find-phase probe returned, with no second <c>/play</c> and no
        /// wait for other probes (docs/PROBE_SCHEDULING_RULES.md rule 12). A failed start
        /// re-<c>/play</c>s once (the cached-URL retry in the orchestrator).
        /// </summary>
        PlayProbedLink,

        /// <summary>
        /// Fresh <c>/play</c> the start row only (its probed link cannot be reused:
        /// <see cref="CanPlayProbedLink"/>); open as soon as it is playable.
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
        /// Recommended is empty or audio-only — try a cached pool row,
        /// else await the partner warm leg.
        /// </summary>
        AwaitPartnerOrCached,

        /// <summary>
        /// No playable URL from this start. Infrastructure on the recommended leg
        /// is terminal here (LocalServiceUnavailable) — do not advance to another row.
        /// </summary>
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
        Func<EnrichedStream, bool>? isWarmOrInFlight = null,
        Func<EnrichedStream, StreamTier>? tierOf = null)
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

        if (CanPlayProbedLink(recommended, now))
        {
            return new StartPlan(
                StartKind.PlayProbedLink,
                PartnerToWarm: null,
                Reason: "play the probed link; a failed start re-/plays once");
        }

        var partner = PlaybackRacePolicy.PickPartner(
            healthy, recommended, now, isWarmOrInFlight, tierOf);

        return new StartPlan(
            StartKind.ResolveRecommendedOpenFirst,
            PartnerToWarm: partner,
            Reason: partner?.Stream is not null
                ? $"open recommended first; warm partner {partner.Stream.Channel} for Next"
                : "open recommended first; no partner to warm");
    }

    /// <summary>
    /// Whether first play may use the link the find-phase probe returned. Only a link
    /// that genuinely cannot be reused needs a fresh <c>/play</c>: no link, a known
    /// single-use source (cookie-bound session or registered single-use host), or a
    /// readable token that has already expired. An unreadable expiry is tried as is
    /// (the probe was just made); a failed start re-<c>/play</c>s once.
    /// </summary>
    public static bool CanPlayProbedLink(EnrichedStream row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Stream is null || string.IsNullOrWhiteSpace(row.ResolvedM3U8Url))
            return false;
        if (PlaybackUrlReusePolicy.IsKnownSingleUse(row))
            return false;
        return M3u8TokenExpiry.RemainingLife(row.ResolvedM3U8Url, now) is not { } left || left > TimeSpan.Zero;
    }

    /// <summary>
    /// Rule 11 (docs/PROBE_SCHEDULING_RULES.md): inside the playback-priority window
    /// (<see cref="PlaybackPriorityTimeout"/> from first play) finding is paused only
    /// while a backup exists that the prewarmer still has to warm. No backup, or a
    /// backup that needs no warm (its probed link is reusable, it is single-use, or its
    /// warm link is already in hand), means finding carries on.
    /// </summary>
    public static bool PlaybackPriorityHolds(int backupsNeedingWarm) => backupsNeedingWarm > 0;

    /// <summary>
    /// Upcoming switch-order rows (up to <see cref="PlaybackUrlReusePolicy.PrewarmDepth"/>)
    /// that the prewarmer would warm now: the same test as <c>UpcomingStreamPrewarmer</c>
    /// (<see cref="PlaybackUrlReusePolicy.ShouldPrewarm"/>, not unwarmable, and
    /// <see cref="PlaybackUrlReusePolicy.NeedsPrewarmRefresh"/> for its warm entry).
    /// </summary>
    public static int CountBackupsNeedingWarm(
        IReadOnlyList<EnrichedStream> healthy,
        EnrichedStream? current,
        DateTimeOffset now,
        Func<EnrichedStream, (DateTimeOffset Expiry, DateTimeOffset ResolvedAt)?> warmEntry,
        Func<EnrichedStream, bool> isUnwarmable)
    {
        ArgumentNullException.ThrowIfNull(healthy);
        return CountBackupsNeedingWarm(
            PlaybackUrlReusePolicy.UpcomingInSwitchOrder(healthy, current, PlaybackUrlReusePolicy.PrewarmDepth),
            now,
            warmEntry,
            isUnwarmable);
    }

    /// <summary>
    /// As above, for the rows the prewarmer would look at, in the order it looks
    /// (<c>IStreamSwitchingService.GetUpcoming</c>: backup order while the start row plays).
    /// </summary>
    public static int CountBackupsNeedingWarm(
        IReadOnlyList<EnrichedStream> upcoming,
        DateTimeOffset now,
        Func<EnrichedStream, (DateTimeOffset Expiry, DateTimeOffset ResolvedAt)?> warmEntry,
        Func<EnrichedStream, bool> isUnwarmable)
    {
        ArgumentNullException.ThrowIfNull(upcoming);
        ArgumentNullException.ThrowIfNull(warmEntry);
        ArgumentNullException.ThrowIfNull(isUnwarmable);

        var n = 0;
        for (var depth = 0; depth < upcoming.Count && depth < PlaybackUrlReusePolicy.PrewarmDepth; depth++)
        {
            var row = upcoming[depth];
            if (!PlaybackUrlReusePolicy.ShouldPrewarm(row) || isUnwarmable(row))
                continue;
            var entry = warmEntry(row);
            if (PlaybackUrlReusePolicy.NeedsPrewarmRefresh(entry?.Expiry, entry?.ResolvedAt, depth, now))
                n++;
        }

        return n;
    }

    /// <summary>
    /// After the recommended fresh leg finishes classifying, whether to open it,
    /// fall back (cached row / partner warm), or fail terminal (infrastructure).
    /// </summary>
    public static AfterRecommendedKind DecideAfterRecommended(RaceLegState recommended) =>
        recommended switch
        {
            RaceLegState.Video or RaceLegState.Unverified => AfterRecommendedKind.OpenRecommended,
            RaceLegState.Empty or RaceLegState.AudioOnly
                => AfterRecommendedKind.AwaitPartnerOrCached,
            // Infrastructure is not a stream verdict, but also not "try the next
            // cached row" — open-recommended must surface LocalServiceUnavailable.
            RaceLegState.Infrastructure => AfterRecommendedKind.Fail,
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
