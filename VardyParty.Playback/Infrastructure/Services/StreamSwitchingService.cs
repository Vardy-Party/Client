using System.Reactive.Subjects;
using VardyParty.Kernel;
using VardyParty.Ports;

namespace VardyParty.Playback;

public class StreamSwitchingService : IStreamSwitchingService, IDisposable
{
    private readonly List<EnrichedStream> _healthyStreams = new();
    private int _currentStreamIndex = -1;
    private string _league = string.Empty;
    private string _homeTeam = string.Empty;
    private string _awayTeam = string.Empty;

    // Healthy-pool dedup (docs/STREAM_PLAYBACK_RULES.md "Healthy pool dedup"): one visible
    // entry per m3u8 identity. The key is captured when a stream enters the pool because
    // ResolvedM3U8Url is mutated afterwards (fresh one-shot /play, cleared on failure).
    private readonly Dictionary<EnrichedStream, string> _identityKeys = new(ReferenceEqualityComparer.Instance);

    // Merged duplicates per identity key. Never shown; promoted in place when the visible
    // entry for that key fails (RemoveCurrentStream).
    private readonly Dictionary<string, List<EnrichedStream>> _fallbacks = new(StringComparer.OrdinalIgnoreCase);

    private static string BuildDedupKey(EnrichedStream stream) =>
        StreamUrlNormalizer.M3u8IdentityKey(stream.ResolvedM3U8Url);

    private static StreamReputation RankOf(EnrichedStream stream) =>
        stream.Stream?.Reputation ?? StreamReputation.None;

    private readonly BehaviorSubject<IReadOnlyList<EnrichedStream>> _healthyStreamsSubject =
        new(new List<EnrichedStream>().AsReadOnly());
    public IObservable<IReadOnlyList<EnrichedStream>> HealthyStreamsUpdated => _healthyStreamsSubject;

    private readonly BehaviorSubject<int> _currentIndexSubject = new(-1);
    public IObservable<int> CurrentStreamIndexChanged => _currentIndexSubject;

    private readonly BehaviorSubject<EnrichedStream?> _currentStreamSubject = new(null);
    public IObservable<EnrichedStream?> CurrentStreamChanged => _currentStreamSubject;

    private readonly BehaviorSubject<PlayerOverlayInfo?> _overlayInfoSubject = new(null);
    public IObservable<PlayerOverlayInfo?> OverlayInfoChanged => _overlayInfoSubject;

    public void Initialize(string league, string homeTeam, string awayTeam)
    {
        _league = league;
        _homeTeam = homeTeam;
        _awayTeam = awayTeam;
        _healthyStreams.Clear();
        _identityKeys.Clear();
        _fallbacks.Clear();
        _currentStreamIndex = -1;
        _healthyStreamsSubject.OnNext(new List<EnrichedStream>().AsReadOnly());
        _currentIndexSubject.OnNext(-1);
        _currentStreamSubject.OnNext(null);
        _overlayInfoSubject.OnNext(null);
    }

    public void AddHealthyStream(EnrichedStream stream)
    {
        lock (_healthyStreams)
        {
            if (_healthyStreams.Any(existing => ReferenceEquals(existing, stream)))
            {
                return;
            }

            // Empty key (no resolved URL) is never a dedup match - collapsing every
            // keyless stream into one would hide distinct sources.
            var incomingKey = BuildDedupKey(stream);
            if (incomingKey.Length > 0)
            {
                var existingIndex = IndexOfKey(incomingKey);
                if (existingIndex >= 0)
                {
                    MergeDuplicate(existingIndex, incomingKey, stream);
                    return;
                }

                _identityKeys[stream] = incomingKey;
            }

            _healthyStreams.Add(stream);

            // If this is the first stream, set it as current
            if (_currentStreamIndex == -1)
            {
                _currentStreamIndex = 0;
                _currentIndexSubject.OnNext(0);
                var cur = _healthyStreams[_currentStreamIndex];
                _currentStreamSubject.OnNext(cur);
            }

            _healthyStreamsSubject.OnNext(_healthyStreams.AsReadOnly());

            // Always update overlay info so UI sees new totals and current stream metadata
            _overlayInfoSubject.OnNext(PlayerOverlayFormatter.BuildOverlayInfo(
                GetCurrentStream(),
                GetCurrentStreamIndex(),
                _healthyStreams.Count));
        }
    }

    private int IndexOfKey(string key)
    {
        for (var i = 0; i < _healthyStreams.Count; i++)
        {
            if (_identityKeys.TryGetValue(_healthyStreams[i], out var existingKey)
                && string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Same media as a visible entry: keep the visible one (first healthy, catalog order)
    /// unless the incoming one has a strictly higher reputation and the visible one is not
    /// playing. The loser is kept as a hidden fallback for that key.
    /// </summary>
    private void MergeDuplicate(int existingIndex, string key, EnrichedStream incoming)
    {
        if (!_fallbacks.TryGetValue(key, out var alternates))
        {
            alternates = new List<EnrichedStream>();
            _fallbacks[key] = alternates;
        }

        if (alternates.Any(a => ReferenceEquals(a, incoming)))
        {
            return;
        }

        var existing = _healthyStreams[existingIndex];
        var outranks = existingIndex != _currentStreamIndex && RankOf(incoming) > RankOf(existing);
        if (!outranks)
        {
            alternates.Add(incoming);
            return;
        }

        _identityKeys.Remove(existing);
        _identityKeys[incoming] = key;
        _healthyStreams[existingIndex] = incoming;
        alternates.Insert(0, existing);

        _healthyStreamsSubject.OnNext(_healthyStreams.AsReadOnly());
        _overlayInfoSubject.OnNext(PlayerOverlayFormatter.BuildOverlayInfo(
            GetCurrentStream(),
            GetCurrentStreamIndex(),
            _healthyStreams.Count));
    }

    /// <summary>Pops the best merged duplicate for <paramref name="failed"/>'s key, if any.</summary>
    private bool TryTakeFallback(EnrichedStream failed, out EnrichedStream promoted)
    {
        promoted = null!;
        if (!_identityKeys.TryGetValue(failed, out var key))
        {
            return false;
        }

        _identityKeys.Remove(failed);
        if (!_fallbacks.TryGetValue(key, out var alternates) || alternates.Count == 0)
        {
            _fallbacks.Remove(key);
            return false;
        }

        var best = 0;
        for (var i = 1; i < alternates.Count; i++)
        {
            if (RankOf(alternates[i]) > RankOf(alternates[best]))
            {
                best = i;
            }
        }

        promoted = alternates[best];
        alternates.RemoveAt(best);
        if (alternates.Count == 0)
        {
            _fallbacks.Remove(key);
        }

        _identityKeys[promoted] = key;
        return true;
    }

    /// <summary>Visible entry for a merged duplicate, or null when it is not a fallback.</summary>
    private EnrichedStream? RepresentativeOf(EnrichedStream stream)
    {
        foreach (var (key, alternates) in _fallbacks)
        {
            if (alternates.Any(a => ReferenceEquals(a, stream)))
            {
                var index = IndexOfKey(key);
                return index >= 0 ? _healthyStreams[index] : null;
            }
        }

        return null;
    }

    /// <summary>Hidden fallbacks merged into the visible entry (test/diagnostic seam).</summary>
    public IReadOnlyList<EnrichedStream> GetMergedFallbacks(EnrichedStream visible)
    {
        lock (_healthyStreams)
        {
            return _identityKeys.TryGetValue(visible, out var key)
                   && _fallbacks.TryGetValue(key, out var alternates)
                ? alternates.ToList().AsReadOnly()
                : Array.Empty<EnrichedStream>();
        }
    }

    public bool SwitchToNextStream()
    {
        lock (_healthyStreams)
        {
            if (_healthyStreams.Count == 0) return false;

            int nextIndex = (_currentStreamIndex + 1) % _healthyStreams.Count;
            return SwitchToStream(nextIndex);
        }
    }

    public bool SwitchToPreviousStream()
    {
        lock (_healthyStreams)
        {
            if (_healthyStreams.Count == 0) return false;

            int prevIndex = (_currentStreamIndex - 1 + _healthyStreams.Count) % _healthyStreams.Count;
            return SwitchToStream(prevIndex);
        }
    }

    public bool SwitchToStream(int index)
    {
        lock (_healthyStreams)
        {
            if (index < 0 || index >= _healthyStreams.Count)
                return false;
            _currentStreamIndex = index;
            _currentIndexSubject.OnNext(index);

            // Publish current stream object as well
            var current = _healthyStreams[_currentStreamIndex];
            _currentStreamSubject.OnNext(current);

            // Publish overlay info for UI/platform consumers
            _overlayInfoSubject.OnNext(PlayerOverlayFormatter.BuildOverlayInfo(
                current,
                GetCurrentStreamIndex(),
                _healthyStreams.Count));

            // No platform-specific actions here - consumers can subscribe to CurrentStreamIndexChanged
            // and HealthyStreamsUpdated to update UI or platform overlays as needed.
            return true;
        }
    }

    public EnrichedStream? GetCurrentStream()
    {
        lock (_healthyStreams)
        {
            if (_currentStreamIndex < 0 || _currentStreamIndex >= _healthyStreams.Count)
                return null;

            return _healthyStreams[_currentStreamIndex];
        }
    }

    public EnrichedStream? GetNextHealthyStream()
    {
        lock (_healthyStreams)
        {
            if (_healthyStreams.Count == 0)
                return null;

            // Get the next stream without switching to it
            int nextIndex = (_currentStreamIndex + 1) % _healthyStreams.Count;
            return nextIndex < _healthyStreams.Count ? _healthyStreams[nextIndex] : null;
        }
    }

    public bool WouldWrapOnNext()
    {
        lock (_healthyStreams)
        {
            if (_healthyStreams.Count == 0 || _currentStreamIndex < 0)
            {
                return false;
            }

            return ((_currentStreamIndex + 1) % _healthyStreams.Count) == 0;
        }
    }

    public void ReorderHealthyStreams(IReadOnlyList<EnrichedStream> preferredFirst)
    {
        lock (_healthyStreams)
        {
            if (_healthyStreams.Count <= 1 || preferredFirst.Count == 0)
            {
                return;
            }

            var current = _currentStreamIndex >= 0 && _currentStreamIndex < _healthyStreams.Count
                ? _healthyStreams[_currentStreamIndex]
                : null;

            var seen = new HashSet<EnrichedStream>();
            var reordered = new List<EnrichedStream>(_healthyStreams.Count);
            foreach (var preferred in preferredFirst)
            {
                // A merged duplicate stands for its visible entry.
                var target = _healthyStreams.Contains(preferred) ? preferred : RepresentativeOf(preferred);
                if (target != null && seen.Add(target))
                {
                    reordered.Add(target);
                }
            }

            foreach (var existing in _healthyStreams)
            {
                if (seen.Add(existing))
                {
                    reordered.Add(existing);
                }
            }

            _healthyStreams.Clear();
            _healthyStreams.AddRange(reordered);
            if (current != null)
            {
                _currentStreamIndex = _healthyStreams.IndexOf(current);
            }

            _healthyStreamsSubject.OnNext(_healthyStreams.AsReadOnly());
            _overlayInfoSubject.OnNext(PlayerOverlayFormatter.BuildOverlayInfo(
                GetCurrentStream(),
                GetCurrentStreamIndex(),
                _healthyStreams.Count));
        }
    }

    public bool SwitchToMatchingStream(string streamUrl, string? streamName)
    {
        lock (_healthyStreams)
        {
            for (var i = 0; i < _healthyStreams.Count; i++)
            {
                if (MatchesCatalogIdentity(_healthyStreams[i].Stream, streamUrl, streamName))
                {
                    return SwitchToStream(i);
                }
            }

            // The chip may name a source merged into another entry (same m3u8).
            foreach (var (key, alternates) in _fallbacks)
            {
                if (alternates.Any(a => MatchesCatalogIdentity(a.Stream, streamUrl, streamName)))
                {
                    var index = IndexOfKey(key);
                    return index >= 0 && SwitchToStream(index);
                }
            }

            return false;
        }
    }

    private static bool MatchesCatalogIdentity(Kernel.Stream stream, string streamUrl, string? streamName)
    {
        if (!string.Equals(stream.Url, streamUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(streamName))
        {
            return true;
        }

        var label = !string.IsNullOrWhiteSpace(stream.PlayerStream)
            ? stream.PlayerStream.Trim()
            : stream.Channel?.Trim();
        return !string.IsNullOrWhiteSpace(label)
               && string.Equals(label, streamName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<EnrichedStream> GetHealthyStreams()
    {
        lock (_healthyStreams)
        {
            return _healthyStreams.AsReadOnly();
        }
    }

    public int GetCurrentStreamIndex()
    {
        lock (_healthyStreams)
        {
            // Return 1-based index for display (0 means none)
            return _currentStreamIndex >= 0 ? _currentStreamIndex + 1 : 0;
        }
    }

    public void Cleanup()
    {
        lock (_healthyStreams)
        {
            _healthyStreams.Clear();
            _identityKeys.Clear();
            _fallbacks.Clear();
            _currentStreamIndex = -1;
            _healthyStreamsSubject.OnNext(new List<EnrichedStream>().AsReadOnly());
            _currentIndexSubject.OnNext(-1);
            _currentStreamSubject.OnNext(null);
            // Also publish null overlay so UI/platform consumers know to clear overlays
            _overlayInfoSubject.OnNext(null);
        }
    }

    public bool RemoveCurrentStream()
    {
        lock (_healthyStreams)
        {
            if (_currentStreamIndex < 0 || _currentStreamIndex >= _healthyStreams.Count) return false;

            // Same media from another source page: swap it in at the same index rather
            // than shrinking the pool (each source gets its own fresh /play token).
            if (TryTakeFallback(_healthyStreams[_currentStreamIndex], out var promoted))
            {
                _healthyStreams[_currentStreamIndex] = promoted;
                _currentIndexSubject.OnNext(_currentStreamIndex);
                _currentStreamSubject.OnNext(promoted);
                _healthyStreamsSubject.OnNext(_healthyStreams.AsReadOnly());
                _overlayInfoSubject.OnNext(PlayerOverlayFormatter.BuildOverlayInfo(
                    promoted,
                    GetCurrentStreamIndex(),
                    _healthyStreams.Count));
                return true;
            }

            _healthyStreams.RemoveAt(_currentStreamIndex);

            // Adjust current index
            if (_healthyStreams.Count == 0)
            {
                _currentStreamIndex = -1;
                _currentIndexSubject.OnNext(-1);
                _currentStreamSubject.OnNext(null);
            }
            else
            {
                // clamp index
                if (_currentStreamIndex >= _healthyStreams.Count) _currentStreamIndex = _healthyStreams.Count - 1;
                _currentIndexSubject.OnNext(_currentStreamIndex);
                _currentStreamSubject.OnNext(_healthyStreams[_currentStreamIndex]);
            }

            _healthyStreamsSubject.OnNext(_healthyStreams.AsReadOnly());

            // Publish updated overlay info
            _overlayInfoSubject.OnNext(PlayerOverlayFormatter.BuildOverlayInfo(
                GetCurrentStream(),
                GetCurrentStreamIndex(),
                _healthyStreams.Count));

            return true;
        }
    }

    public void Dispose()
    {
        _healthyStreamsSubject?.Dispose();
        _currentIndexSubject?.Dispose();
        _currentStreamSubject?.Dispose();
    }
}
