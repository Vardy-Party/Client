using System;

namespace VardyParty.Hosting;

public static class PlaybackHttpClients
{
    public const string Probe = "PlaybackProbe";
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// DualStack client used by the Desktop LibVLC referer bridge to fetch
    /// playlists and segments on LibVLC's behalf (WSL field failure: native
    /// libvlc HTTP demux cancelled while this stack succeeded health checks).
    /// </summary>
    public const string LibVlcBridge = "LibVlcBridge";
    public static readonly TimeSpan LibVlcBridgeTimeout = TimeSpan.FromMinutes(2);
}
