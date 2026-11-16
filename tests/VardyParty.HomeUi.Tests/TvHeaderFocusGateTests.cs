using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public class TvHeaderFocusGateTests
{
    [Fact]
    public void ShouldHoldHeaderOnMenuWire_Phone_NeverHolds()
    {
        Assert.False(TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: false,
            boardHasCards: false,
            initialFocusPending: false,
            streamExitPending: false));
        Assert.False(TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: false,
            boardHasCards: true,
            initialFocusPending: true,
            streamExitPending: true));
    }

    [Fact]
    public void ShouldHoldHeaderOnMenuWire_ColdStart_HoldsBeforeFirstCard()
    {
        // Cold start: Menu wires before rails exist. Hold so Android's
        // default search cannot land on Menu and leave the first rail off-screen.
        Assert.True(TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: true,
            boardHasCards: false,
            initialFocusPending: false,
            streamExitPending: false));
    }

    [Fact]
    public void ShouldHoldHeaderOnMenuWire_ColdStart_HoldsWhileInitialFocusPending()
    {
        Assert.True(TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: true,
            boardHasCards: true,
            initialFocusPending: true,
            streamExitPending: false));
    }

    [Fact]
    public void ShouldRearmInitialFocusOnMenuWire_NewHomeViewWhenVmAlreadyHasGames()
    {
        // Singleton HomeViewModel already consumed RequestsInitialFocus on
        // an earlier view (CreateWindow / second MainActivity.OnCreate).
        Assert.True(TvHeaderFocusGate.ShouldRearmInitialFocusOnMenuWire(
            isTelevision: true,
            boardHasCards: true,
            initialFocusPending: false));
        Assert.True(TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: true,
            boardHasCards: true,
            initialFocusPending: false,
            streamExitPending: false));
    }

    [Fact]
    public void ShouldRearmInitialFocusOnMenuWire_DoesNotRearmWhenStillPendingOrEmpty()
    {
        Assert.False(TvHeaderFocusGate.ShouldRearmInitialFocusOnMenuWire(
            isTelevision: true,
            boardHasCards: true,
            initialFocusPending: true));
        Assert.False(TvHeaderFocusGate.ShouldRearmInitialFocusOnMenuWire(
            isTelevision: true,
            boardHasCards: false,
            initialFocusPending: false));
        Assert.False(TvHeaderFocusGate.ShouldRearmInitialFocusOnMenuWire(
            isTelevision: false,
            boardHasCards: true,
            initialFocusPending: false));
    }

    [Fact]
    public void ShouldRearmInitialFocusWhenAuthOverlayHides_AfterGamesLoaded()
    {
        Assert.True(TvHeaderFocusGate.ShouldRearmInitialFocusWhenAuthOverlayHides(
            isTelevision: true,
            boardHasCards: true));
        Assert.False(TvHeaderFocusGate.ShouldRearmInitialFocusWhenAuthOverlayHides(
            isTelevision: true,
            boardHasCards: false));
        Assert.False(TvHeaderFocusGate.ShouldRearmInitialFocusWhenAuthOverlayHides(
            isTelevision: false,
            boardHasCards: true));
    }

    [Fact]
    public void ShouldAllowUpToMenu_BlockedOnlyWhileHoldHasNoCardAndNotTimedOut()
    {
        Assert.False(TvHeaderFocusGate.ShouldAllowUpToMenu(
            holdActive: true,
            anyCardHasHadFocus: false,
            holdTimedOut: false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void ShouldAllowUpToMenu_OnceCardFocusedOrTimedOutOrHoldReleased(
        bool holdActive, bool anyCardHasHadFocus, bool holdTimedOut)
    {
        Assert.True(TvHeaderFocusGate.ShouldAllowUpToMenu(
            holdActive, anyCardHasHadFocus, holdTimedOut));
    }

    [Fact]
    public void IsHoldTimedOut_UsesBoundedTimeout()
    {
        const long start = 1_000;
        var timeout = TvHeaderFocusGate.HoldTimeoutMilliseconds;
        Assert.True(timeout >= 5_000);
        Assert.False(TvHeaderFocusGate.IsHoldTimedOut(start, start + timeout - 1, timeout));
        Assert.True(TvHeaderFocusGate.IsHoldTimedOut(start, start + timeout, timeout));
    }
}
