namespace VardyParty.Streaming;

/// <summary>
/// Extra probe data from LocalService <c>POST /mp</c>. Playlist-relative
/// segments on MP CDNs are HTML; health must hit rewritten media URLs instead.
/// </summary>
public sealed class StreamHealthProbe
{
    public IReadOnlyList<string>? RewrittenSegmentUrls { get; init; }

    public IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }
}
