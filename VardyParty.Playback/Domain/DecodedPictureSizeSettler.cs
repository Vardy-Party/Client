namespace VardyParty.Playback;

/// <summary>
/// Holds back a decoded picture size until it has stayed the same for
/// <see cref="Settle"/>. Windows MediaPlayer / AMS often reports 1920x1080
/// for 100–200ms before the real size arrives; publishing that first sample
/// flashes 1080p on the overlay. Android and Linux use the same rule so a
/// first-size flash cannot appear on any platform. Until a size settles,
/// the overlay should keep showing a bitstream-probed height if one exists.
/// Mid-play the picture size can change (a CDN or encoder moving 720p to 1080p).
/// The new size replaces the published one once it has held for the same
/// <see cref="Settle"/>; until then the previous size stays published, so a brief
/// blip never reaches the overlay. Hosts copy each newly published size into the
/// stream's stored height with <see cref="MeasuredPictureSize.Remember"/>.
/// </summary>
public sealed class DecodedPictureSizeSettler
{
    public static readonly TimeSpan Settle = PlaybackPolicy.DecodedPictureSettle;

    private readonly Func<DateTimeOffset> _clock;
    private int _pendingWidth;
    private int _pendingHeight;
    private DateTimeOffset _pendingSince;
    private bool _hasPending;
    private int _publishedWidth;
    private int _publishedHeight;
    private bool _published;

    public DecodedPictureSizeSettler(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The settled decoded size, or <c>null</c> until one has stayed for <see cref="Settle"/>.</summary>
    public (int Width, int Height)? Published =>
        _published ? (_publishedWidth, _publishedHeight) : null;

    public bool HasPublished => _published;

    /// <summary>True while a sample is waiting to stay unchanged for <see cref="Settle"/>.</summary>
    public bool NeedsTick =>
        _hasPending && (!_published || _pendingWidth != _publishedWidth || _pendingHeight != _publishedHeight);

    /// <summary>Drop pending and published sizes (call at the start of a switch / new attach).</summary>
    public void Reset()
    {
        _hasPending = false;
        _published = false;
        _pendingWidth = 0;
        _pendingHeight = 0;
        _publishedWidth = 0;
        _publishedHeight = 0;
    }

    /// <summary>
    /// Record a natural-video-size sample. Zero dimensions are ignored.
    /// Returns the settled size, or <c>null</c> if no size has stayed for
    /// <see cref="Settle"/> yet (including a first 1920x1080 flash). A new size
    /// mid-play returns the previous settled size until the new one has held.
    /// </summary>
    public (int Width, int Height)? Observe(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return Published;

        var now = _clock();
        if (!_hasPending || width != _pendingWidth || height != _pendingHeight)
        {
            _pendingWidth = width;
            _pendingHeight = height;
            _pendingSince = now;
            _hasPending = true;
            // Keep the previously published size until the new one has held for Settle.
            return Published;
        }

        return MaybePublish(now);
    }

    /// <summary>Re-evaluate the pending sample against the clock without a new size.</summary>
    public (int Width, int Height)? Tick()
    {
        if (!_hasPending)
            return Published;
        return MaybePublish(_clock());
    }

    private (int Width, int Height)? MaybePublish(DateTimeOffset now)
    {
        if (NeedsTick && now - _pendingSince >= Settle)
        {
            _publishedWidth = _pendingWidth;
            _publishedHeight = _pendingHeight;
            _published = true;
        }

        return Published;
    }
}
