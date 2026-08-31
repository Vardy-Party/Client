using VardyParty.Hosting;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class LinuxSnapSideloadTests
{
    [Fact]
    public void BuildWaitInstallRelaunchScript_WaitsThenSideloadsAndRelaunches()
    {
        // Arrange / Act
        var script = LinuxSnapSideload.BuildWaitInstallRelaunchScript();

        // Assert
        Assert.Contains("trap '' HUP", script, System.StringComparison.Ordinal);
        Assert.Contains("kill -0", script, System.StringComparison.Ordinal);
        Assert.Contains("pkexec snap install --dangerous --classic", script, System.StringComparison.Ordinal);
        Assert.Contains($"snap run {LinuxSnapSideload.SnapName}", script, System.StringComparison.Ordinal);
        Assert.DoesNotContain("snap refresh", script, System.StringComparison.Ordinal);
    }
}
