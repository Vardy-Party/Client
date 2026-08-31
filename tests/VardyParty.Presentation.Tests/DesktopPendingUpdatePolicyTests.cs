using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class DesktopPendingUpdatePolicyTests
{
    private static readonly AppReleaseVersion Running = new(2, 0, 0, 159);

    [Fact]
    public void Evaluate_NoPending_IsNone()
    {
        // Arrange / Act
        var state = DesktopPendingUpdatePolicy.Evaluate(Running, pendingExpected: null);

        // Assert
        Assert.Equal(DesktopPendingUpdateState.None, state);
    }

    [Fact]
    public void Evaluate_RunningMatchesPending_IsApplied()
    {
        // Arrange
        var expected = new AppReleaseVersion(2, 1, 0, 160);

        // Act
        var state = DesktopPendingUpdatePolicy.Evaluate(expected, expected);

        // Assert
        Assert.Equal(DesktopPendingUpdateState.Applied, state);
    }

    [Fact]
    public void Evaluate_RunningNewerThanPending_IsApplied()
    {
        // Arrange
        var pending = new AppReleaseVersion(2, 0, 0, 159);
        var running = new AppReleaseVersion(2, 1, 0, 160);

        // Act
        var state = DesktopPendingUpdatePolicy.Evaluate(running, pending);

        // Assert
        Assert.Equal(DesktopPendingUpdateState.Applied, state);
    }

    [Fact]
    public void Evaluate_RunningStillOlder_IsFailedToApply()
    {
        // Arrange
        var pending = new AppReleaseVersion(2, 1, 0, 160);

        // Act
        var state = DesktopPendingUpdatePolicy.Evaluate(Running, pending);

        // Assert
        Assert.Equal(DesktopPendingUpdateState.FailedToApply, state);
    }
}
