using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Recommended tiers first (docs/PROBE_SCHEDULING_RULES.md rule 1; Jon, 13 Nov 2026).
/// <list type="bullet">
/// <item>Top tier: every High-confidence recommended row (a recommended MP page or chip
/// included). For <see cref="Duration"/> the top tier has both LocalService slots to itself.</item>
/// <item>Second tier: Medium-confidence recommended rows. They come next, before any
/// finding. During the head start a Medium row may start only when every High row has a
/// verdict or there are fewer than two High rows (so a slot the top tier cannot use does
/// not sit idle).</item>
/// <item>Finding: Low recommendations, MP pages and FB rows. No finding probe
/// (<c>/mp</c> included) starts during the head start while a High or Medium row has no
/// verdict. After it, a slot not used by High goes to Medium first, then finding (MP
/// lane first): an MP finding row waits while a High or Medium FB row is still queued.</item>
/// </list>
/// After that, first come, first served applies. Shared by <see cref="StreamResolver"/>
/// (which applies it), <see cref="StreamResolutionOrchestrator"/> and the probe timeline harness.
/// </summary>
public sealed class TopTierHeadStart : IDisposable
{
    /// <summary>How long the top tier has LocalService to itself.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(12);

    private readonly HashSet<StreamModel> _rows;
    private readonly HashSet<StreamModel> _medium;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<TopTierHeadStart>? _onDispose;
    private volatile bool _findingHeld;

    internal TopTierHeadStart(
        IEnumerable<StreamModel> high,
        IEnumerable<StreamModel> medium,
        TimeProvider time,
        Action<TopTierHeadStart>? onDispose = null)
    {
        _rows = new HashSet<StreamModel>(high, ReferenceEqualityComparer.Instance);
        _medium = new HashSet<StreamModel>(medium, ReferenceEqualityComparer.Instance);
        _medium.ExceptWith(_rows);
        _onDispose = onDispose;
        Elapsed = Task.Delay(Duration, time, _cts.Token)
            .ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Completes when <see cref="Duration"/> has passed (or the head start was disposed).</summary>
    public Task Elapsed { get; }

    public bool IsElapsed => Elapsed.IsCompleted;

    /// <summary>Number of High (top-tier) rows.</summary>
    public int Count => _rows.Count;

    public int MediumCount => _medium.Count;

    /// <summary>A High-confidence recommended row.</summary>
    public bool IsTopTier(StreamModel? row) => row is not null && _rows.Contains(row);

    /// <summary>A Medium-confidence recommended row (the second tier).</summary>
    public bool IsMedium(StreamModel? row) => row is not null && _medium.Contains(row);

    /// <summary>
    /// Set by the resolver: finding rows are queued but held by the head start right now.
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
