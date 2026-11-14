using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// Jon, 13 Nov 2026 (Windows 251): "restore from full-screen on windows by double click....
/// window will full screen dimensions". Leaving full screen must put the window back where it was.
/// </summary>
public class FullScreenRestoreTrackerTests
{
    private static readonly WindowRect Normal = new(200, 150, 1280, 720);
    private static readonly WindowRect Monitor = new(0, 0, 2560, 1440);
    private static readonly WindowRect WorkArea = new(0, 0, 2560, 1392);

    private static WindowRect? Work(WindowRect _) => WorkArea;

    [Fact]
    public void Rule_LeavingFullScreen_RestoresThePreviousSizeAndPosition()
    {
        // Arrange
        var tracker = new FullScreenRestoreTracker();
        tracker.Observe(WindowPlacementMode.Restored, Normal, Work);
        tracker.Observe(WindowPlacementMode.FullScreen, Monitor, Work);

        // Act - the presenter is back to overlapped but still full-screen sized (the bug).
        var plan = tracker.Observe(WindowPlacementMode.Restored, Monitor, Work);

        // Assert
        Assert.Equal(new FullScreenRestorePlan(Normal, Maximize: false), plan);
    }

    [Fact]
    public void Rule_PlanRepeats_UntilTheWindowIsReallyBack_ThenStops()
    {
        // Arrange - the first MoveAndResize lands while the presenter is still switching and is dropped.
        var tracker = new FullScreenRestoreTracker();
        tracker.Observe(WindowPlacementMode.Restored, Normal, Work);
        tracker.Observe(WindowPlacementMode.FullScreen, Monitor, Work);
        tracker.Observe(WindowPlacementMode.Restored, Monitor, Work);

        // Act
        var again = tracker.Observe(WindowPlacementMode.Restored, Monitor, Work);
        var done = tracker.Observe(WindowPlacementMode.Restored, Normal with { Width = Normal.Width + 1 }, Work);

        // Assert
        Assert.NotNull(again);
        Assert.Null(done);
        Assert.Null(tracker.Pending);
    }

    [Fact]
    public void Rule_RepeatedToggles_KeepTheNormalBounds_NotTheFullScreenSize()
    {
        // Arrange
        var tracker = new FullScreenRestoreTracker();
        tracker.Observe(WindowPlacementMode.Restored, Normal, Work);

        for (var i = 0; i < 3; i++)
        {
            // Act
            tracker.Observe(WindowPlacementMode.FullScreen, Monitor, Work);
            var plan = tracker.Observe(WindowPlacementMode.Restored, Monitor, Work);
            tracker.Observe(WindowPlacementMode.Restored, Normal, Work);

            // Assert
            Assert.Equal(Normal, plan?.Bounds);
            Assert.Equal(Normal, tracker.RestoredBounds);
        }
    }

    [Fact]
    public void Rule_MaximizedBeforeFullScreen_ComesBackMaximized_OverItsNormalBounds()
    {
        // Arrange
        var tracker = new FullScreenRestoreTracker();
        tracker.Observe(WindowPlacementMode.Restored, Normal, Work);
        tracker.Observe(WindowPlacementMode.Maximized, WorkArea, Work);
        tracker.Observe(WindowPlacementMode.FullScreen, Monitor, Work);

        // Act
        var plan = tracker.Observe(WindowPlacementMode.Restored, Monitor, Work);
        var midway = tracker.Observe(WindowPlacementMode.Restored, Normal, Work);
        var done = tracker.Observe(WindowPlacementMode.Maximized, WorkArea, Work);

        // Assert - normal bounds first, so un-maximizing later returns to them.
        Assert.Equal(new FullScreenRestorePlan(Normal, Maximize: true), plan);
        Assert.NotNull(midway);
        Assert.Null(done);
        Assert.Equal(Normal, tracker.RestoredBounds);
    }

    [Fact]
    public void Rule_GivesUpAfterMaxAttempts_AndNeverRemembersTheFullScreenSize()
    {
        // Arrange
        var tracker = new FullScreenRestoreTracker();
        tracker.Observe(WindowPlacementMode.Restored, Normal, Work);
        tracker.Observe(WindowPlacementMode.FullScreen, Monitor, Work);

        // Act
        var plans = 0;
        for (var i = 0; i < FullScreenRestoreTracker.MaxApplyAttempts + 3; i++)
        {
            if (tracker.Observe(WindowPlacementMode.Restored, Monitor, Work) is not null)
                plans++;
        }

        // Assert
        Assert.Equal(FullScreenRestoreTracker.MaxApplyAttempts, plans);
        Assert.Null(tracker.Pending);
    }

    [Fact]
    public void Rule_NoKnownNormalBounds_AndNotMaximized_LeavesTheWindowAlone()
    {
        // Arrange - started full screen.
        var tracker = new FullScreenRestoreTracker();
        tracker.Observe(WindowPlacementMode.FullScreen, Monitor, Work);

        // Act
        var plan = tracker.Observe(WindowPlacementMode.Restored, Monitor, Work);

        // Assert
        Assert.Null(plan);
    }

    [Fact]
    public void Rule_SavedBoundsOffTheCurrentDisplay_AreFittedIntoItsWorkArea()
    {
        // Arrange - the window was on a second monitor that is gone (or the resolution dropped).
        var tracker = new FullScreenRestoreTracker();
        tracker.Observe(WindowPlacementMode.Restored, new WindowRect(3000, 100, 1280, 720), _ => null);
        tracker.Observe(WindowPlacementMode.FullScreen, Monitor, Work);

        // Act
        var plan = tracker.Observe(WindowPlacementMode.Restored, Monitor, Work);

        // Assert
        Assert.Equal(new WindowRect(1280, 100, 1280, 720), plan?.Bounds);
    }

    [Theory]
    [InlineData(100, 100, 800, 600, 100, 100, 800, 600)]
    [InlineData(-50, -20, 800, 600, 0, 0, 800, 600)]
    [InlineData(0, 0, 4000, 3000, 0, 0, 2560, 1392)]
    public void Fit_KeepsTheWindowInsideTheWorkArea(int x, int y, int w, int h, int ex, int ey, int ew, int eh)
        => Assert.Equal(new WindowRect(ex, ey, ew, eh), FullScreenRestoreTracker.Fit(new WindowRect(x, y, w, h), WorkArea));

    [Fact]
    public void Minimized_IsNeitherRememberedNorFought()
    {
        // Arrange
        var tracker = new FullScreenRestoreTracker();
        tracker.Observe(WindowPlacementMode.Restored, Normal, Work);

        // Act
        var plan = tracker.Observe(WindowPlacementMode.Minimized, new WindowRect(-32000, -32000, 160, 28), Work);

        // Assert
        Assert.Null(plan);
        Assert.Equal(Normal, tracker.RestoredBounds);
    }
}