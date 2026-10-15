namespace VardyParty.Streaming;

/// <summary>
/// Applies strategy playlist rewrites (V2 CTU → absolute KDNS URLs) to a
/// fetched HLS body. V1 plugins return the input unchanged.
/// </summary>
public interface IPlaybackPlaylistProcessor
{
    string Process(string playlistText);
}
