namespace VardyParty.Presentation;

/// <summary>
/// Tracks which homepage overlays are currently visible so hardware Back
/// suppression exactly follows visible UI instead of one anonymous boolean.
/// The old aggregated flag lived in a process-lifetime Android static that was
/// never reset on activity recreation, so a session that ended mid-overlay
/// (device-code sign-in, stream resolution) left Back suppressed on the next
/// idle homepage with no way to tell which overlay was to blame.
/// Thread-safe: overlay state changes arrive from background continuations.
/// </summary>
public sealed class OverlayBackSuppressionTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _visible = new(StringComparer.Ordinal);

    public bool IsSuppressed
    {
        get
        {
            lock (_gate)
            {
                return _visible.Count > 0;
            }
        }
    }

    /// <summary>Report an overlay's current visibility. Idempotent.</summary>
    public void Set(string overlay, bool visible)
    {
        if (string.IsNullOrWhiteSpace(overlay))
        {
            return;
        }

        lock (_gate)
        {
            if (visible)
            {
                _visible.Add(overlay);
            }
            else
            {
                _visible.Remove(overlay);
            }
        }
    }

    /// <summary>
    /// Clear all overlay state. Called when a fresh activity is created so
    /// suppression can never leak across sessions via process-lifetime statics.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _visible.Clear();
        }
    }

    /// <summary>Stable, log-friendly list of the overlays currently suppressing Back.</summary>
    public string DescribeActive()
    {
        lock (_gate)
        {
            return _visible.Count == 0
                ? "none"
                : string.Join("+", _visible.OrderBy(name => name, StringComparer.Ordinal));
        }
    }
}
