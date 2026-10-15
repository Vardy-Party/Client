using VardyParty.LocalService.Abstractions;

namespace VardyParty.Streaming;

public sealed class TransportPluginPlaylistProcessor(
    IEnumerable<IPlaybackTransportPlugin> plugins) : IPlaybackPlaylistProcessor
{
    public string Process(string playlistText)
    {
        if (string.IsNullOrEmpty(playlistText))
            return playlistText;

        foreach (var plugin in plugins)
            playlistText = plugin.PostProcessPlaylist(playlistText);

        return playlistText;
    }
}
