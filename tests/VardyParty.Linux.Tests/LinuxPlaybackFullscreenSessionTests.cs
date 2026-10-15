using VardyParty.Linux.Services;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LinuxPlaybackFullscreenSessionTests
{
    [Fact]
    public void ResolveEnterTarget_Wsl_IsTestHostMaximizeOnly()
    {
        // Arrange
        // Act
        var mode = LinuxPlaybackFullscreenSession.ResolveEnterTarget(_ => null, isWsl: true);

        // Assert
        Assert.Equal(LinuxHostWindowMode.Maximized, mode);
    }

    [Fact]
    public void ResolveEnterTarget_Native_DefaultsToFullScreen()
    {
        // Arrange
        // Act
        var mode = LinuxPlaybackFullscreenSession.ResolveEnterTarget(_ => null, isWsl: false);

        // Assert
        Assert.Equal(LinuxHostWindowMode.FullScreen, mode);
    }

    [Fact]
    public void ResolveEnterTarget_EnvZero_ForcesFullScreenOnWsl()
    {
        // Arrange
        // Act
        var mode = LinuxPlaybackFullscreenSession.ResolveEnterTarget(
            name => name == LinuxPlaybackFullscreenSession.MaximizeInsteadEnv ? "0" : null,
            isWsl: true);

        // Assert
        Assert.Equal(LinuxHostWindowMode.FullScreen, mode);
    }

    [Fact]
    public void Toggle_EnterExit_RestoresPriorMode()
    {
        // Arrange
        var sut = new LinuxPlaybackFullscreenSession();

        // Act
        var entered = sut.Enter(LinuxHostWindowMode.Normal, _ => null, isWsl: false);
        var exited = sut.Exit();

        // Assert
        Assert.Equal(LinuxHostWindowMode.FullScreen, entered);
        Assert.Equal(LinuxHostWindowMode.Normal, exited);
        Assert.False(sut.IsFullscreen);
    }
}
