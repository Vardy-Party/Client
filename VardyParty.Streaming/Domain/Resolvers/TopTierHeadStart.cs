using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Recommended tiers first (docs/PROBE_SCHEDULING_RULES.md rule 1; Jon, 13 Nov 2026).
/// <list type="bullet">
/// <item>Top tier: every High-confidence recommended row (a recommended MP page or chip
/// included; the chips LocalService lists for a High page inherit High). For
/// <see cref="Duration"/> High rows may use both LocalService slots: a free slot goes to a
/// waiting High row first.</item>
/// <item>Second tier: Medium-confidence recommended rows (and the chips of a Medium page).
/// Medium rows use at most one slot at a time, always (Jon: "Medium limited to one slot
/// always"; the cap lasts the session through <see cref="StreamTiers"/>): a slot no High row
/// wants goes to a Medium row while no other Medium row runs (a slot a dead High row frees
/// goes to Medium at once).</item>
/// <item>Finding: Low recommendations, MP pages and FB rows. Finding fills every slot High
/// and Medium leave, from the first second, MP lane (<c>/mp</c>) first, then FB
/// <c>/play</c> (Jon: "high can occupy 2 slots on local-service, medium only one, then fit
/// in v1 and v2 into gaps").</item>
/// </list>
/// After <see cref="Duration"/>, first come, first served in try-order, still with one Medium
/// row at a time (<see cref="ResolverRaceThrottle.CurrentTiers"/> keeps the tiers). Shared by <see cref="StreamResolver"/>
/// (which applies it), <see cref="StreamResolutionOrchestrator"/> and the probe timeline harness.
/// </summary>
public sealed class TopTierHeadStart : IDisposable
{
    /// <summary>How long the top tier has LocalService to itself.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(12);

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<TopTierHeadStart>? _onDispose;
    private volatile bool _findingHeld;

    internal TopTierHeadStart(
        IEnumerable<StreamModel> high,
        IEnumerable<StreamModel> medium,
        TimeProvider time,
        Action<TopTierHeadStart>? onDispose = null)
    {
        Tiers = new StreamTiers(high, medium);
        _onDispose = onDispose;
        Elapsed = Task.Delay(Duration, time, _cts.Token)
            .ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Completes when <see cref="Duration"/> has passed (or the head start was disposed).</summary>
    public Task Elapsed { get; }

    public bool IsElapsed => Elapsed.IsCompleted;

    /// <summary>The session's tiers (chips inherit their page's tier as they are listed).</summary>
    public StreamTiers Tiers { get; }

    /// <summary>Number of High (top-tier) rows.</summary>
    public int Count => Tiers.HighCount;

    public int MediumCount => Tiers.MediumCount;

    /// <summary>A High-confidence recommended row (or a chip of a High page).</summary>
    public bool IsTopTier(StreamModel? row) => Tiers.IsHigh(row);

    /// <summary>A Medium-confidence recommended row (the second tier).</summary>
    public bool IsMedium(StreamModel? row) => Tiers.IsMedium(row);

    /// <summary>
    /// Set by the resolver: rows are queued but held by the head start right now (see also
    /// <see cref="StreamTiers.RowsHeld"/>, which covers the Medium cap after it).
    /// Read-only seam for the probe timeline harness (A3 excludes this time).
    /// </summary>
    public bool FindingHeld
    {
        get => _findingHeld;
        internal set => _findingHeld = value;
    }

    /// <summary>
    /// The tiers: the leading try-order rows that match a High-confidence recommendation,
    /// then the rows after them that match a Medium one (<see cref="StreamTestOrderPolicy.Build"/>
    /// orders High, Medium, Low). Both empty when nothing is recommended High or Medium:
    /// behaviour is then unchanged.
    /// </summary>
    public static (IReadOnlyList<StreamModel> High, IReadOnlyList<StreamModel> Medium) Select(
        RecommendationResponse? recommendations,
        IReadOnlyList<StreamModel> ordered)
    {
        var recs = recommendations?.Recommended?
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .ToList() ?? [];
        var high = recs.Where(r => r.Confidence == RecommendationConfidence.High).ToList();
        var medium = recs.Where(r => r.Confidence == RecommendationConfidence.Medium).ToList();
        var i = 0;
        var top = Leading(ordered, high, ref i);
        var second = Leading(ordered, medium, ref i);
        return (top, second);
    }

    private static List<StreamModel> Leading(IReadOnlyList<StreamModel> ordered, List<RecommendationItem> recs, ref int i)
    {
        var rows = new List<StreamModel>();
        for (; i < ordered.Count && rows.Count < recs.Count; i++)
        {
            var row = ordered[i];
            if (!recs.Any(r => Matches(row, r)))
                break;
            rows.Add(row);
        }

        return rows;
    }

    // A recommendation naming an MP chip also matches its unlabeled catalog page row
    // (the row LocalService expands into chips), as the try-order does.
    private static bool Matches(StreamModel row, RecommendationItem rec) =>
        StreamHealthIdentity.MatchesRecommendation(row, rec.Url, rec.StreamName)
        || (string.IsNullOrWhiteSpace(row.PlayerStream) && StreamHealthIdentity.MatchesRecommendation(row, rec.Url, null));

    public void Dispose()
    {
        _findingHeld = false;
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        _onDispose?.Invoke(this);
    }
}
