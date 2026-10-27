namespace VardyParty.Kernel;

public enum PlaybackSwitchKind
{
    /// <summary>Nothing playable on the row.</summary>
    None,

    /// <summary>Play the row's cached URL now; a failed start re-/plays once.</summary>
    AttachCached,

    /// <summary>Resolve via <c>/play</c> (or a warm pre-resolve) while the current stream keeps playing.</summary>
    ResolveFresh,
}

public readonly record struct PlaybackSwitchPlan(PlaybackSwitchKind Kind, string? Url, string Reason);

/// <summary>
/// Cached-vs-fresh m3u8 decisions shared by every player host and the orchestrator.
/// <para>
/// One-shot rows (<see cref="RequiresFreshM3u8ForPlayback"/>: soak headers or rewritten media)
/// re-/play on attach because find-phase health may burn the token (5604455). A one-shot
/// row may still reuse its cached URL when the token expiry is readable, more than
/// <see cref="MinComfortableLife"/> remains, and the row is not <see cref="IsKnownSingleUse"/>
/// (Cookie-bound session, <c>/mp</c> rewritten media, or a registered single-use host).
/// Unreadable expiry stays on the fresh path.
/// </para>
/// </summary>
public static class PlaybackUrlReusePolicy
{
    /// <summary>Token life a cached or warm URL needs to be played without a new <c>/play</c>.</summary>
    public static readonly TimeSpan MinComfortableLife = TimeSpan.FromSeconds(60);

    /// <summary>Warm URLs are refreshed this long before they stop being comfortable.</summary>
    public static readonly TimeSpan PrewarmRefreshLead = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Tokens issued for less than this (cowedd ~2 min) are kept warm only for the
    /// immediate next stream, so LocalService is not re-scraped every minute for depth 2.
    /// </summary>
    public static readonly TimeSpan LongLivedTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>How many upcoming streams in switch order are pre-resolved.</summary>
    public const int PrewarmDepth = 2;

    private static readonly object HostGate = new();
    private static string[] _singleUseHostSuffixes = [];

    /// <summary>Marks a CDN host (suffix match) whose m3u8 tokens burn on first use.</summary>
    public static void RegisterSingleUseHost(string hostSuffix)
    {
        if (string.IsNullOrWhiteSpace(hostSuffix))
            return;
        lock (HostGate)
        {
            _singleUseHostSuffixes = [.. _singleUseHostSuffixes, hostSuffix.Trim().TrimStart('.')];
        }
    }

    internal static void ResetSingleUseHostsForTests()
    {
        lock (HostGate)
        {
            _singleUseHostSuffixes = [];
        }
    }

    /// <summary>
    /// Pool row already carries soak headers / rewritten samples from find -
    /// treat as one-shot FB/V1 (or V2) and re-resolve before the player unless reusable.
    /// </summary>
    public static bool RequiresFreshM3u8ForPlayback(EnrichedStream? stream)
    {
        if (stream is null)
            return false;

        if (MpRewrittenSegment.HasProbeReady(stream.RewrittenSegments))
            return true;

        return HasPlaybackSoakHeaders(stream.RequestHeaders);
    }

    /// <summary>Cookie, Referer or Origin with a value.</summary>
    public static bool HasPlaybackSoakHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return false;

        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;

            if (pair.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("referer", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("origin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sources whose token is bound to one fetch or one browser session: never reuse,
    /// never pre-resolve.
    /// </summary>
    public static bool IsKnownSingleUse(EnrichedStream? stream)
    {
        if (stream is null)
            return false;

        if (MpRewrittenSegment.HasProbeReady(stream.RewrittenSegments))
            return true;

        if (stream.RequestHeaders is { } headers
            && headers.Any(pair => pair.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value)))
        {
            return true;
        }

        return IsSingleUseHost(stream.ResolvedM3U8Url);
    }

    public static bool IsSingleUseHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host;
        foreach (var suffix in Volatile.Read(ref _singleUseHostSuffixes))
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasComfortableLife(string? url, DateTimeOffset now)
        => M3u8TokenExpiry.RemainingLife(url, now) is { } left && left > MinComfortableLife;

    /// <summary>
    /// True when a one-shot row's cached URL may be played straight away (with the
    /// usual one fresh retry on a failed start).
    /// </summary>
    public static bool CanReuseCached(EnrichedStream? stream, DateTimeOffset now)
        => stream?.Stream is not null
            && !string.IsNullOrWhiteSpace(stream.ResolvedM3U8Url)
            && !IsKnownSingleUse(stream)
            && HasComfortableLife(stream.ResolvedM3U8Url, now);

    /// <summary>Decision for an index-driven switch onto <paramref name="current"/>.</summary>
    public static PlaybackSwitchPlan PlanSwitch(EnrichedStream? current, DateTimeOffset now)
    {
        if (current?.Stream is null)
            return new PlaybackSwitchPlan(PlaybackSwitchKind.None, null, "no current stream");

        var url = current.ResolvedM3U8Url;
        if (!RequiresFreshM3u8ForPlayback(current))
        {
            return string.IsNullOrWhiteSpace(url)
                ? new PlaybackSwitchPlan(PlaybackSwitchKind.None, null, "no cached URL")
                : new PlaybackSwitchPlan(PlaybackSwitchKind.AttachCached, url, "not one-shot");
        }

        if (CanReuseCached(current, now))
        {
            var left = M3u8TokenExpiry.RemainingLife(url, now)!.Value;
            return new PlaybackSwitchPlan(
                PlaybackSwitchKind.AttachCached,
                url,
                $"token has {(int)left.TotalSeconds}s left");
        }

        return new PlaybackSwitchPlan(
            PlaybackSwitchKind.ResolveFresh,
            null,
            IsKnownSingleUse(current) ? "known single-use token" : "token expiry unknown or too close");
    }

    /// <summary>Rows the background pre-resolver may keep warm.</summary>
    public static bool ShouldPrewarm(EnrichedStream? stream)
        => stream?.Stream is not null
            && RequiresFreshM3u8ForPlayback(stream)
            && !IsKnownSingleUse(stream);

    /// <summary>
    /// Whether the pre-resolver should (re)resolve a candidate at <paramref name="depth"/>
    /// (0 = immediate next) given its warm entry, if any.
    /// </summary>
    public static bool NeedsPrewarmRefresh(
        DateTimeOffset? warmExpiry,
        DateTimeOffset? warmResolvedAt,
        int depth,
        DateTimeOffset now)
    {
        if (warmExpiry is not { } expiry)
            return true;

        if (expiry - now > MinComfortableLife + PrewarmRefreshLead)
            return false;

        if (depth > 0
            && warmResolvedAt is { } resolvedAt
            && expiry - resolvedAt < LongLivedTokenLifetime)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Up to <paramref name="depth"/> visible pool rows after <paramref name="current"/>
    /// in Next order (wrapping), never the current row.
    /// </summary>
    public static IReadOnlyList<EnrichedStream> UpcomingInSwitchOrder(
        IReadOnlyList<EnrichedStream> healthy,
        EnrichedStream? current,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(healthy);
        var result = new List<EnrichedStream>();
        if (healthy.Count == 0 || depth <= 0)
            return result;

        var start = -1;
        for (var i = 0; i < healthy.Count; i++)
        {
            if (ReferenceEquals(healthy[i], current))
            {
                start = i;
                break;
            }
        }

        for (var step = 1; step <= healthy.Count && result.Count < depth; step++)
        {
            var row = healthy[(start + step + healthy.Count) % healthy.Count];
            if (ReferenceEquals(row, current) || result.Contains(row))
                continue;
            result.Add(row);
        }

        return result;
    }

    public static string SwitchPendingText(string? channel)
        => $"Switching to {Label(channel)}\u2026";

    public static string SwitchFailedText(string? channel)
        => $"Couldn't switch to {Label(channel)} - staying on the current stream";

    private static string Label(string? channel)
        => string.IsNullOrWhiteSpace(channel) ? "next stream" : channel.Trim();
}
