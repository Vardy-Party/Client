using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Option 2 switch-back on a master-URL row: the master body never changes, so the
/// probe follows one variant and the frozen check judges that media playlist.
/// </summary>
public class SwitchBackMasterPlaylistTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T20:00:00Z");
    private const string MasterUrl = "https://cdn.example.com/live/channel-north/master.m3u8";
    private const string Master =
        "#EXTM3U\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow/index.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720\nhigh/index.m3u8\n";

    private static string Media(int sequence) =>
        $"#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:{sequence}\n#EXTINF:6.0,\nseg{sequence}.ts?token=t\n";

    private static SwitchBackCandidate Candidate() =>
        new(MasterUrl, "Channel North", "channel-north", 1080, null, null);

    private sealed class ScriptedHandler(Func<string, string> bodyFor) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requested.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bodyFor(url), Encoding.UTF8, "application/vnd.apple.mpegurl"),
            });
        }
    }

    private static async Task<SwitchBackDecision> TwoProbesAsync(Func<int, string> variantBody, List<string>? requested = null)
    {
        var probeNumber = 0;
        var handler = new ScriptedHandler(url => url == MasterUrl ? Master : variantBody(probeNumber));
        var probe = new HttpSwitchBackProbe(new HttpClient(handler));
        var session = new SwitchBackSession();
        session.Arm(Candidate(), T0);
        session.NoteBackupHeight(648, T0);

        probeNumber = 1;
        var first = await probe.ProbeAsync(Candidate(), CancellationToken.None);
        session.NoteProbe(first.Playable, first.StatusCode, T0.AddSeconds(5), first.Progress);
        probeNumber = 2;
        var second = await probe.ProbeAsync(Candidate(), CancellationToken.None);
        requested?.AddRange(handler.Requested);
        return session.NoteProbe(second.Playable, second.StatusCode, T0.AddSeconds(20), second.Progress);
    }

    [Fact]
    public async Task IdenticalMasterBodies_MovingVariant_CountAsRecovered()
    {
        // Arrange
        var requested = new List<string>();

        // Act
        var decision = await TwoProbesAsync(n => Media(40 + n), requested);

        // Assert
        Assert.Equal(SwitchBackAction.SwitchBack, decision.Action);
        Assert.Contains("https://cdn.example.com/live/channel-north/high/index.m3u8", requested);
        Assert.DoesNotContain("https://cdn.example.com/live/channel-north/low/index.m3u8", requested);
    }

    [Fact]
    public async Task IdenticalMasterBodies_FrozenVariant_DoNotCountAsRecovered()
    {
        // Arrange
        static string Frozen(int _) => Media(41);

        // Act
        var decision = await TwoProbesAsync(Frozen);

        // Assert
        Assert.Equal(SwitchBackAction.Wait, decision.Action);
        Assert.Equal("playlist not moving", decision.Reason);
    }

    [Fact]
    public void Progress_OfMasterPlaylist_IsNull()
    {
        // Arrange
        var body = Encoding.UTF8.GetBytes(Master);

        // Act
        var progress = SwitchBackPlaylistProgress.Of(body);

        // Assert
        Assert.Null(progress);
    }

    [Fact]
    public void VariantUrl_PicksHighestBandwidth_ResolvedAgainstMaster()
    {
        // Arrange
        var body = Encoding.UTF8.GetBytes(Master);

        // Act
        var variant = SwitchBackPlaylistProgress.VariantUrl(body, MasterUrl);
        var notMaster = SwitchBackPlaylistProgress.VariantUrl(Encoding.UTF8.GetBytes(Media(1)), MasterUrl);

        // Assert
        Assert.Equal("https://cdn.example.com/live/channel-north/high/index.m3u8", variant);
        Assert.Null(notMaster);
    }
}
