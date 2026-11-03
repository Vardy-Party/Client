namespace VardyParty.Kernel;

/// <summary>What a race leg knows when the winner rule is applied.</summary>
public enum RaceLegState
{
    /// <summary>The <c>/play</c> or the playlist read is still running.</summary>
    Pending,

    /// <summary>The playlist proves there is picture.</summary>
    Video,

    /// <summary>A URL came back and the playlist did not prove audio-only. The player still confirms picture.</summary>
    Unverified,

    /// <summary>The playlist proves there is no picture (0x0 or audio-only segments).</summary>
    AudioOnly,

    /// <summary>The service answered with no playlist: a verdict on the stream.</summary>
    Empty,

    /// <summary>The service could not answer: no verdict.</summary>
    Infrastructure,
}

public enum RaceDecision
{
    /// <summary>Keep waiting.</summary>
    Wait,

    /// <summary>Play the recommended row.</summary>
    Recommended,

    /// <summary>Play the partner row.</summary>
    Partner,

    /// <summary>
    /// The recommended row is a verdict (empty or audio-only) and some other pooled row
    /// can play without another <c>/play</c>. Do not wait for the partner.
    /// </summary>
    PlayReadyRow,

    /// <summary>Neither leg produced a playable URL.</summary>
    NoWinner,
}

/// <summary>
/// Helpers for first-play playlist legs: <see cref="NeedsFreshPlay"/>,
/// <see cref="PickPartner"/>, <see cref="LoserTakeBy"/>, and <see cref="RaceLegState"/>.
/// First picture + early Next capacity is owned by <see cref="FirstPlayOpenStrategy"/>
/// (open recommended first; partner warms for Next). The blocking dual-leg
/// <see cref="PlaybackUrlRacer"/> wait is no longer used to open the player.
/// </summary>
public static class PlaybackRacePolicy
{
    /// <summary>How long a partner waits for the recommended row, and how long an unverified URL waits for a proven-video partner.</summary>
    public static readonly TimeSpan RecommendedGrace = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long a losing URL with no readable token expiry stays usable.</summary>
    public static readonly TimeSpan LoserFallbackTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether starting <paramref name="row"/> costs a fresh <c>/play</c>: no cached URL,
    /// or a one-shot row whose cached URL cannot be reused.
    /// </summary>
    public static bool NeedsFreshPlay(EnrichedStream? row, DateTimeOffset now)
    {
        if (row?.Stream is null)
            return false;
        if (string.IsNullOrWhiteSpace(row.ResolvedM3U8Url))
            return true;
        return PlaybackUrlReusePolicy.RequiresFreshM3u8ForPlayback(row)
            && !PlaybackUrlReusePolicy.CanReuseCached(row, now);
    }

    /// <summary>
    /// The row to race against <paramref name="recommended"/>: the next row in switch
    /// order, when it also needs a fresh <c>/play</c> and is not already warm or being
    /// pre-resolved. Null means start the recommended row alone — a partner that can
    /// play its cached URL is already an instant fallback and must not be waited on.
    /// </summary>
    public static EnrichedStream? PickPartner(
        IReadOnlyList<EnrichedStream> healthy,
        EnrichedStream recommended,
        DateTimeOffset now,
        Func<EnrichedStream, bool>? isWarmOrInFlight = null)
    {
        ArgumentNullException.ThrowIfNull(healthy);
        ArgumentNullException.ThrowIfNull(recommended);
        if (!NeedsFreshPlay(recommended, now))
            return null;

        var next = PlaybackUrlReusePolicy.UpcomingInSwitchOrder(healthy, recommended, 1);
        if (next.Count == 0)
            return null;

        var partner = next[0];
        if (ReferenceEquals(partner, recommended)
            || partner.Stream is null
            || string.Equals(partner.Stream.Url, recommended.Stream?.Url, StringComparison.OrdinalIgnoreCase)
            || !NeedsFreshPlay(partner, now)
            || isWarmOrInFlight?.Invoke(partner) == true)
        {
            return null;
        }

        return partner;
    }

    /// <summary>
    /// The winner rule. Proven video on the recommended row always wins. Proven video on
    /// the partner beats an unverified recommended URL. An audio-only playlist never wins.
    /// When the recommended row is already a verdict and <paramref name="otherRowCanPlayNow"/>
    /// is set, play that row instead of waiting for the partner (the finding-streams dialog
    /// must not sit on a dead <c>/play</c> while a cached row is ready).
    /// </summary>
    public static RaceDecision Decide(
        RaceLegState recommended,
        RaceLegState partner,
        bool graceElapsed,
        bool otherRowCanPlayNow = false)
    {
        if (recommended == RaceLegState.Video)
            return RaceDecision.Recommended;

        if (recommended == RaceLegState.Unverified)
        {
            if (partner == RaceLegState.Video)
                return RaceDecision.Partner;
            if (partner == RaceLegState.Pending && !graceElapsed)
                return RaceDecision.Wait;
            return RaceDecision.Recommended;
        }

        if (recommended == RaceLegState.Pending)
        {
            if (partner is RaceLegState.Video or RaceLegState.Unverified && graceElapsed)
                return RaceDecision.Partner;
            return RaceDecision.Wait;
        }

        // Recommended is empty, audio-only, or infrastructure: no URL to play.
        if (partner is RaceLegState.Video or RaceLegState.Unverified)
            return RaceDecision.Partner;

        if (otherRowCanPlayNow && recommended is RaceLegState.Empty or RaceLegState.AudioOnly)
            return RaceDecision.PlayReadyRow;

        if (partner == RaceLegState.Pending)
            return RaceDecision.Wait;

        if (otherRowCanPlayNow && recommended == RaceLegState.Infrastructure)
            return RaceDecision.PlayReadyRow;

        return RaceDecision.NoWinner;
    }

    /// <summary>
    /// Last moment a never-played race URL may be taken: its token expiry minus
    /// <see cref="PlaybackUrlReusePolicy.MinComfortableLife"/>, else
    /// <see cref="LoserFallbackTtl"/> from <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset LoserTakeBy(string? url, DateTimeOffset now)
        => M3u8TokenExpiry.TryGetExpiry(url, out var expiry)
            ? expiry - PlaybackUrlReusePolicy.MinComfortableLife
            : now + LoserFallbackTtl;
}
