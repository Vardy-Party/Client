using Xunit;

namespace VardyParty.Hosting.Tests;

/// <summary>
/// Live run: the local-service was restarted for a new package, the Windows
/// client kept polling status every 2s, saw <c>sharing:false</c>, and never
/// started the host again, so the phone found no relay host.
/// </summary>
public class HostWatchTests
{
    [Fact]
    public void RestartedLocalService_StartsTheHostAgain()
    {
        Assert.Equal(HostWatchAction.Restart, HostWatch.Decide(reachable: true, sharing: false, inviteUsed: false, inviteOutstanding: false));
        Assert.Equal(HostWatchAction.Restart, HostWatch.Decide(reachable: true, sharing: false, inviteUsed: true, inviteOutstanding: true));
    }

    [Fact]
    public void HostingWithAnOutstandingInvite_KeepsPolling()
    {
        Assert.Equal(HostWatchAction.Keep, HostWatch.Decide(reachable: true, sharing: true, inviteUsed: false, inviteOutstanding: true));
    }

    [Fact]
    public void RedeemedInvite_IsClearedOnce()
    {
        Assert.Equal(HostWatchAction.InviteUsed, HostWatch.Decide(reachable: true, sharing: true, inviteUsed: true, inviteOutstanding: true));
        Assert.Equal(HostWatchAction.Keep, HostWatch.Decide(reachable: true, sharing: true, inviteUsed: true, inviteOutstanding: false));
    }

    [Fact]
    public void OlderLocalServiceWithoutASharingField_KeepsPolling()
    {
        Assert.Equal(HostWatchAction.Keep, HostWatch.Decide(reachable: true, sharing: null, inviteUsed: false, inviteOutstanding: false));
    }

    [Fact]
    public void UnreachableLocalService_WaitsForItToComeBack()
    {
        Assert.Equal(HostWatchAction.Offline, HostWatch.Decide(reachable: false, sharing: null, inviteUsed: false, inviteOutstanding: true));
    }
}
