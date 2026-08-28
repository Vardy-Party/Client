using System;

namespace VardyParty.Hosting;

public static class PlaybackHttpClients
{
    public const string Probe = "PlaybackProbe";
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);
}
