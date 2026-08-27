using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public sealed class HomeBackDecisionTests
{
    private static readonly long Grace = (long)HomeBackDecision.ExitGrace.TotalMilliseconds;

    [Fact]
    public void OverlayVisible_DelegatesToOverlays()
    {
        var action = HomeBackDecision.Decide(overlaySuppressed: true, lastOverlayBackMs: 0, nowMs: 10_000);

        Assert.Equal(HomeBackDecision.BackAction.DelegateToOverlays, action);
    }

    [Fact]
    public void OverlayVisible_DelegatesEvenInsideTheGraceWindow()
    {
        // A second overlay press (menu still open) keeps delegating — the
        // grace only guards the app-exit branch.
        var action = HomeBackDecision.Decide(overlaySuppressed: true, lastOverlayBackMs: 9_900, nowMs: 10_000);

        Assert.Equal(HomeBackDecision.BackAction.DelegateToOverlays, action);
    }

    [Fact]
    public void NothingOpen_NoRecentOverlayBack_ExitsApp()
    {
        var action = HomeBackDecision.Decide(overlaySuppressed: false, lastOverlayBackMs: 0, nowMs: 10_000);

        Assert.Equal(HomeBackDecision.BackAction.ExitApp, action);
    }

    [Fact]
    public void NothingOpen_WithinGraceOfOverlayClose_IsIgnored()
    {
        // The double-press race: press 1 closed the menu (suppression cleared
        // synchronously) but the saturated TV panel still SHOWS an open menu;
        // press 2 lands ~1s later and must not exit the app.
        var action = HomeBackDecision.Decide(overlaySuppressed: false, lastOverlayBackMs: 10_000, nowMs: 10_000 + Grace - 1);

        Assert.Equal(HomeBackDecision.BackAction.IgnoreStaleExit, action);
    }

    [Fact]
    public void NothingOpen_ExactlyAtGraceBoundary_ExitsApp()
    {
        var action = HomeBackDecision.Decide(overlaySuppressed: false, lastOverlayBackMs: 10_000, nowMs: 10_000 + Grace);

        Assert.Equal(HomeBackDecision.BackAction.ExitApp, action);
    }

    [Fact]
    public void NothingOpen_LongAfterOverlayClose_ExitsApp()
    {
        var action = HomeBackDecision.Decide(overlaySuppressed: false, lastOverlayBackMs: 10_000, nowMs: 60_000);

        Assert.Equal(HomeBackDecision.BackAction.ExitApp, action);
    }

    [Fact]
    public void GraceCoversTheObservedWorstCaseFramePass()
    {
        // Field evidence: full-tree passes of ~1.3s. A repeat press against a
        // stale frame arrives within that window, so the grace must exceed it.
        Assert.True(HomeBackDecision.ExitGrace.TotalMilliseconds > 1_300);
    }
}
