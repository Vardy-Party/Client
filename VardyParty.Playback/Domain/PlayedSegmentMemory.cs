namespace VardyParty.Playback;

/// <summary>
/// Segments this viewing has already fetched (host + path; the signed query is not
/// part of the identity). Shared by Android, Windows and Linux: each adapter calls
/// <see cref="NoteSegment"/> from its segment download path. The first media playlist
/// served after every attach — including a same-URL reattach, a fresh /play of the
/// same channel and a switch-back — drops the leading segments already fetched
/// (<see cref="PlaylistReloadSession.DropPlayedSegments"/>), so playback resumes at or
/// after the last segment shown and never replays watched video.
/// Owned by <see cref="PlaybackSessionController.PlayedSegments"/> for the player's life.
/// </summary>
public sealed class PlayedSegmentMemory
{
    /// <summary>Most recent segment identities kept (oldest are forgotten first).</summary>
    public const int MaxIds = 500;

    private readonly object _gate = new();
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private int _trimOnce;

    public int Count
    {
        get
        {
            lock (_gate)
                return _ids.Count;
        }
    }

    /// <summary>The next media playlist served drops played segments even mid-attach (Android stall re-prepare, playlist redirect).</summary>
    public void ArmTrimOnce() => Interlocked.Exchange(ref _trimOnce, 1);

    internal bool ConsumeTrim() => Interlocked.Exchange(ref _trimOnce, 0) == 1;

    /// <summary>Records a fetched media segment. Playlists (.m3u8) are ignored.</summary>
    public void NoteSegment(string? url)
    {
        var identity = UnplayedPlaylist.SegmentIdentity(url);
        if (identity.Length == 0
            || identity.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_gate)
        {
            if (!_ids.Add(identity))
                return;
            _order.Enqueue(identity);
            while (_order.Count > MaxIds && _order.TryDequeue(out var expired))
                _ids.Remove(expired);
        }
    }

    public bool WasPlayed(string url)
    {
        var identity = UnplayedPlaylist.SegmentIdentity(url);
        lock (_gate)
            return identity.Length > 0 && _ids.Contains(identity);
    }
}
