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

    private static string BuildDedupKey(EnrichedStream stream) =>
        StreamUrlNormalizer.NormalizeForDedup(stream.ResolvedM3U8Url);

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
            var incomingKey = BuildDedupKey(stream);
            var duplicate = _healthyStreams.Any(existing =>
                string.Equals(BuildDedupKey(existing), incomingKey, StringComparison.OrdinalIgnoreCase));

            if (duplicate)
            {
                return;
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
            var current = GetCurrentStream();
            var overlay = current == null ? null : new PlayerOverlayInfo
            {
                Index = GetCurrentStreamIndex(),
                Total = _healthyStreams.Count,
                Channel = current.Stream?.Channel,
                BitrateKbps = current.Stream?.BitrateKbps ?? current.Health?.Bitrate,
                Resolution = current.Stream?.Resolution ?? current.Health?.Resolution,
                FrameRate = current.Health?.FrameRate != null ? (double?)current.Health.FrameRate : null,
                VideoCodec = null,
                AudioCodec = null,
                AspectRatio = PlayerOverlayFormatter.BuildAspect(current.Stream?.Resolution ?? current.Health?.Resolution),
                Title = current.Stream?.Channel
            };
            _overlayInfoSubject.OnNext(overlay);
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
            var overlay = new PlayerOverlayInfo
            {
                Index = GetCurrentStreamIndex(),
                Total = _healthyStreams.Count,
                Channel = current.Stream?.Channel,
                BitrateKbps = current.Stream?.BitrateKbps ?? current.Health?.Bitrate,
                Resolution = current.Stream?.Resolution ?? current.Health?.Resolution,
                FrameRate = current.Health?.FrameRate != null ? (double?)current.Health.FrameRate : null,
                VideoCodec = null,
                AudioCodec = null,
                AspectRatio = PlayerOverlayFormatter.BuildAspect(current.Stream?.Resolution ?? current.Health?.Resolution),
                Title = current.Stream?.Channel
            };
            _overlayInfoSubject.OnNext(overlay);

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
            var current = GetCurrentStream();
            var overlay = current == null ? null : new PlayerOverlayInfo
            {
                Index = GetCurrentStreamIndex(),
                Total = _healthyStreams.Count,
                Channel = current.Stream?.Channel,
                BitrateKbps = current.Stream?.BitrateKbps ?? current.Health?.Bitrate,
                Resolution = current.Stream?.Resolution ?? current.Health?.Resolution,
                FrameRate = current.Health?.FrameRate != null ? (double?)current.Health.FrameRate : null,
                VideoCodec = null,
                AudioCodec = null,
                AspectRatio = PlayerOverlayFormatter.BuildAspect(current.Stream?.Resolution ?? current.Health?.Resolution),
                Title = current.Stream?.Channel
            };
            _overlayInfoSubject.OnNext(overlay);

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
