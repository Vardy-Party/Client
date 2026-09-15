using System;
using System.Threading;

namespace VardyParty.Hosting;

public static class PlaybackHttpClients
{
    public const string Probe = "PlaybackProbe";
    public const string Media = "PlaybackMedia";
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Android ExoPlayer / <c>ManagedHttpDataSource</c> uses this client with
    /// <c>ResponseHeadersRead</c>. Media3 <c>Open</c>/<c>Close</c> owns stall
    /// detection; a finite <see cref="HttpClient.Timeout"/> is a whole-operation
    /// budget that is easy to regress into cutting mid-stream.
    /// </summary>
    public static readonly TimeSpan MediaTimeout = Timeout.InfiniteTimeSpan;
}
