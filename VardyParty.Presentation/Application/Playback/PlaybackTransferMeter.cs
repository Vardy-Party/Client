namespace VardyParty.Presentation;

/// <summary>
/// Bitrate from bytes the player actually read, over a short window.
/// Playlist <c>BANDWIDTH</c> tags are often missing on a media playlist.
/// </summary>
public sealed class PlaybackTransferMeter
{
    /// <summary>How long a window must run before a bitrate is published.</summary>
    public const long WindowMs = 2000;

    /// <summary>How many completed windows the displayed rate averages.</summary>
    public const int AverageWindows = 4;

    /// <summary>
    /// A window below this is playlist or header noise, not video.
    /// 3 kbps was a 2s window of a few hundred bytes before the first segment.
    /// </summary>
    public const int MinimumKbps = 64;

    private readonly object _gate = new();
    private readonly Queue<int> _samples = new();
    private long _windowBytes;
    private long _windowStartMs;
    private bool _open;
    private int _kbps;

    /// <summary>Average of the recent video windows, in kilobits per second, or null before the first one.</summary>
    public int? BitrateKbps
    {
        get
        {
            lock (_gate)
                return _kbps > 0 ? _kbps : null;
        }
    }

    /// <summary>Forget the previous stream's rate.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _windowBytes = 0;
            _windowStartMs = 0;
            _open = false;
            _kbps = 0;
            _samples.Clear();
        }
    }

    /// <summary>Add bytes the player read at <paramref name="nowMs"/> (monotonic milliseconds).</summary>
    public void Add(int bytes, long nowMs)
    {
        if (bytes <= 0)
            return;

        lock (_gate)
        {
            if (!_open)
            {
                _open = true;
                _windowStartMs = nowMs;
                _windowBytes = bytes;
                return;
            }

            _windowBytes += bytes;
            var elapsed = nowMs - _windowStartMs;
            if (elapsed < WindowMs)
                return;

            var sample = (int)(_windowBytes * 8L / elapsed);
            _windowBytes = 0;
            _windowStartMs = nowMs;
            if (sample < MinimumKbps)
                return;

            _samples.Enqueue(sample);
            while (_samples.Count > AverageWindows)
                _samples.Dequeue();
            _kbps = (int)_samples.Average();
        }
    }
}
