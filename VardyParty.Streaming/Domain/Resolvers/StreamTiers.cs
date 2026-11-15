using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Which rows are recommended High or Medium for this session (docs/PROBE_SCHEDULING_RULES.md
/// rule 1), by row reference. A chip of a recommended MP page inherits the page's tier
/// (<see cref="Inherit"/>). Outlives the head start: the backup preference (rule 11) reads
/// it through <see cref="TierOf(EnrichedStream)"/>, and the resolver keeps the Medium cap
/// (one LocalService slot, always) in every pass of the session. Thread-safe.
/// </summary>
public sealed class StreamTiers
{
    private readonly object _gate = new();
    private readonly HashSet<StreamModel> _high;
    private readonly HashSet<StreamModel> _medium;
    private volatile bool _rowsHeld;

    public StreamTiers(IEnumerable<StreamModel> high, IEnumerable<StreamModel> medium)
    {
        _high = new HashSet<StreamModel>(high, ReferenceEqualityComparer.Instance);
        _medium = new HashSet<StreamModel>(medium, ReferenceEqualityComparer.Instance);
        _medium.ExceptWith(_high);
    }

    public int HighCount
    {
        get { lock (_gate) return _high.Count; }
    }

    public int MediumCount
    {
        get { lock (_gate) return _medium.Count; }
    }

    public bool IsHigh(StreamModel? row)
    {
        if (row is null)
            return false;
        lock (_gate)
            return _high.Contains(row);
    }

    public bool IsMedium(StreamModel? row)
    {
        if (row is null)
            return false;
        lock (_gate)
            return _medium.Contains(row);
    }

    public StreamTier TierOf(StreamModel? row) =>
        IsHigh(row) ? StreamTier.High : IsMedium(row) ? StreamTier.Medium : StreamTier.Finding;

    public StreamTier TierOf(EnrichedStream row) => TierOf(row?.Stream);

    /// <summary>
    /// Chips of <paramref name="page"/> (an MP page row, or its seed chip) take its tier:
    /// High for a High page, Medium for a Medium page. A chip that already has a tier keeps
    /// it; chips of a finding page stay finding.
    /// </summary>
    public void Inherit(StreamModel page, IEnumerable<StreamModel> chips)
    {
        lock (_gate)
        {
            var target = _high.Contains(page) ? _high : _medium.Contains(page) ? _medium : null;
            if (target is null)
                return;
            foreach (var chip in chips)
            {
                if (!_high.Contains(chip) && !_medium.Contains(chip))
                    target.Add(chip);
            }
        }
    }

    /// <summary>
    /// Set by the resolver: a queued row is waiting on a tier rule right now (the head start,
    /// or the Medium cap: a second Medium row waits for the first). Read-only seam for the
    /// probe timeline harness (A3 excludes this time).
    /// </summary>
    public bool RowsHeld
    {
        get => _rowsHeld;
        internal set => _rowsHeld = value;
    }

    /// <summary>Snapshot of the High rows (diagnostics and the probe timeline harness).</summary>
    public IReadOnlyList<StreamModel> HighRows
    {
        get { lock (_gate) return _high.ToList(); }
    }

    /// <summary>Snapshot of the Medium rows.</summary>
    public IReadOnlyList<StreamModel> MediumRows
    {
        get { lock (_gate) return _medium.ToList(); }
    }
}
