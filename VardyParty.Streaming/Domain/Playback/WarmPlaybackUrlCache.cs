using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// Pre-resolved (<c>/play</c>) m3u8 responses for upcoming pool rows, keyed by row
/// reference. An entry is unplayed and therefore unburned; it is consumed once by
/// <see cref="TryTake"/>. Only responses with a readable token expiry are stored
/// (<see cref="M3u8TokenExpiry"/>), so a warm hit is never older than its token allows.
/// </summary>
public sealed class WarmPlaybackUrlCache
{
    private readonly object _gate = new();
    private readonly Dictionary<EnrichedStream, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<EnrichedStream, TaskCompletionSource> _inFlight = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<EnrichedStream> _unwarmable = new(ReferenceEqualityComparer.Instance);

    public sealed record Entry(M3U8Response Response, DateTimeOffset Expiry, DateTimeOffset ResolvedAt);

    /// <summary>Stores a fresh response. False (and the row is marked unwarmable) when no expiry is readable.</summary>
    public bool Put(EnrichedStream stream, M3U8Response response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            if (!M3u8TokenExpiry.TryGetExpiry(response.Url, out var expiry))
            {
                _entries.Remove(stream);
                _unwarmable.Add(stream);
                return false;
            }

            _entries[stream] = new Entry(response, expiry, now);
            return true;
        }
    }

    public Entry? Peek(EnrichedStream stream)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(stream, out var entry) ? entry : null;
        }
    }

    /// <summary>True when an earlier pre-resolve returned a URL with no readable expiry.</summary>
    public bool IsUnwarmable(EnrichedStream stream)
    {
        lock (_gate)
        {
            return _unwarmable.Contains(stream);
        }
    }

    /// <summary>
    /// Consumes the warm entry when it still has comfortable life left
    /// (<see cref="PlaybackUrlReusePolicy.MinComfortableLife"/>). Stale entries are dropped.
    /// </summary>
    public bool TryTake(EnrichedStream stream, DateTimeOffset now, out M3U8Response response)
    {
        lock (_gate)
        {
            response = null!;
            if (!_entries.Remove(stream, out var entry))
                return false;
            if (entry.Expiry - now <= PlaybackUrlReusePolicy.MinComfortableLife)
                return false;
            response = entry.Response;
            return true;
        }
    }

    /// <summary>Marks a pre-resolve as running for <paramref name="stream"/>. False if one already is.</summary>
    public bool BeginInFlight(EnrichedStream stream)
    {
        lock (_gate)
        {
            if (_inFlight.ContainsKey(stream))
                return false;
            _inFlight[stream] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public void EndInFlight(EnrichedStream stream)
    {
        TaskCompletionSource? tcs;
        lock (_gate)
        {
            if (!_inFlight.Remove(stream, out tcs))
                return;
        }

        tcs.TrySetResult();
    }

    /// <summary>
    /// Waits for a running pre-resolve of <paramref name="stream"/> so a switch joins it
    /// instead of issuing a second <c>/play</c>. Returns immediately when none is running.
    /// </summary>
    public async Task WaitForInFlightAsync(EnrichedStream stream, CancellationToken cancellationToken)
    {
        Task? pending;
        lock (_gate)
        {
            pending = _inFlight.TryGetValue(stream, out var tcs) ? tcs.Task : null;
        }

        if (pending != null)
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops entries for rows no longer in the visible pool.</summary>
    public void Prune(IReadOnlyCollection<EnrichedStream> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var live = new HashSet<EnrichedStream>(keep, ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => !live.Contains(k)).ToList())
                _entries.Remove(key);
            _unwarmable.RemoveWhere(k => !live.Contains(k));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _unwarmable.Clear();
        }
    }
}
