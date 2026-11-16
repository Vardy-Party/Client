using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public class TvHeaderFocusGateTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public void ShouldHoldHeaderOnMenuWire_Phone_NeverHolds(
        bool boardHasCards, bool initialFocusPending, bool streamExitPending)
    {
        // Arrange
        const bool isTelevision = false;

        // Act
        var hold = TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision, boardHasCards, initialFocusPending, streamExitPending);

        // Assert
        Assert.False(hold);
    }

    [Fact]
    public void ShouldHoldHeaderOnMenuWire_ColdStart_HoldsBeforeFirstCard()
    {
        // Arrange - cold start: Menu wires before rails exist. Hold so Android's
        // default search cannot land on Menu and leave the first rail off-screen.
        const bool boardHasCards = false;

        // Act
        var hold = TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: true, boardHasCards, initialFocusPending: false, streamExitPending: false);

        // Assert
        Assert.True(hold);
    }

    [Fact]
    public void ShouldHoldHeaderOnMenuWire_ColdStart_HoldsWhileInitialFocusPending()
    {
        // Arrange
        const bool initialFocusPending = true;

        // Act
        var hold = TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: true, boardHasCards: true, initialFocusPending, streamExitPending: false);

        // Assert
        Assert.True(hold);
    }

    [Fact]
    public void ShouldHoldHeaderOnMenuWire_NewHomeViewWhenVmAlreadyHasGames_Holds()
    {
        // Arrange - the singleton HomeViewModel already consumed RequestsInitialFocus
        // on an earlier view; the caller re-arms, so the hold has a release path.
        const bool initialFocusPending = false;

        // Act
        var hold = TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: true, boardHasCards: true, initialFocusPending, streamExitPending: false);

        // Assert
        Assert.True(hold);
    }

    [Fact]
    public void ShouldHoldHeaderOnMenuWire_StreamExitPending_Holds()
    {
        // Arrange
        const bool streamExitPending = true;

        // Act
        var hold = TvHeaderFocusGate.ShouldHoldHeaderOnMenuWire(
            isTelevision: true, boardHasCards: true, initialFocusPending: false, streamExitPending);

        // Assert
        Assert.True(hold);
    }

    [Fact]
    public void ShouldRearmInitialFocusOnMenuWire_NewHomeViewWhenVmAlreadyHasGames_Rearms()
    {
        // Arrange - CreateWindow / second MainActivity.OnCreate on the singleton VM.
        const bool initialFocusPending = false;

        // Act
        var rearm = TvHeaderFocusGate.ShouldRearmInitialFocusOnMenuWire(
            isTelevision: true, boardHasCards: true, initialFocusPending, streamExitPending: false);

        // Assert
        Assert.True(rearm);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ShouldRearmInitialFocusOnMenuWire_StillPendingEmptyOrPhone_DoesNotRearm(
        bool isTelevision, bool boardHasCards, bool initialFocusPending)
    {
        // Arrange
        const bool streamExitPending = false;

        // Act
        var rearm = TvHeaderFocusGate.ShouldRearmInitialFocusOnMenuWire(
            isTelevision, boardHasCards, initialFocusPending, streamExitPending);

        // Assert
        Assert.False(rearm);
    }

    [Fact]
    public void ShouldRearmInitialFocusOnMenuWire_StreamExitPending_DoesNotRearm()
    {
        // Arrange - Menu re-wires while the stream-exit pass is focusing the
        // watched game; a first-card retry would race it.
        const bool streamExitPending = true;

        // Act
        var rearm = TvHeaderFocusGate.ShouldRearmInitialFocusOnMenuWire(
            isTelevision: true, boardHasCards: true, initialFocusPending: false, streamExitPending);

        // Assert
        Assert.False(rearm);
    }

    [Fact]
    public void DecideWhenAuthOverlayHides_CardsLoaded_RearmsNow()
    {
        // Arrange - device-code sign-in finished after the catalog arrived.
        const bool boardHasCards = true;

        // Act
        var action = TvHeaderFocusGate.DecideWhenAuthOverlayHides(
            isTelevision: true, boardHasCards, boardSettledEmpty: false, streamExitPending: false);

        // Assert
        Assert.Equal(TvAuthOverlayHiddenAction.RearmNow, action);
    }

    [Fact]
    public void DecideWhenAuthOverlayHides_AuthenticatedColdStartBeforeCatalog_KeepsHoldUntilCards()
    {
        // Arrange - HomeHostPage: StartGamesFeed(); SetAuthOverlayVisible(false);
        // the board is empty and still loading, so Menu must stay out (#113).
        const bool boardHasCards = false;
        const bool boardSettledEmpty = false;

        // Act
        var action = TvHeaderFocusGate.DecideWhenAuthOverlayHides(
            isTelevision: true, boardHasCards, boardSettledEmpty, streamExitPending: false);

        // Assert
        Assert.Equal(TvAuthOverlayHiddenAction.HoldUntilCards, action);
    }

    [Fact]
    public void DecideWhenAuthOverlayHides_BoardSettledEmpty_Releases()
    {
        // Arrange - nothing can arm a first card, so Settings must stay reachable.
        const bool boardSettledEmpty = true;

        // Act
        var action = TvHeaderFocusGate.DecideWhenAuthOverlayHides(
            isTelevision: true, boardHasCards: false, boardSettledEmpty, streamExitPending: false);

        // Assert
        Assert.Equal(TvAuthOverlayHiddenAction.Release, action);
    }

    [Fact]
    public void DecideWhenAuthOverlayHides_StreamExitPending_LeavesTheHoldToStreamExit()
    {
        // Arrange
        const bool streamExitPending = true;

        // Act
        var action = TvHeaderFocusGate.DecideWhenAuthOverlayHides(
            isTelevision: true, boardHasCards: true, boardSettledEmpty: false, streamExitPending);

        // Assert
        Assert.Equal(TvAuthOverlayHiddenAction.None, action);
    }

    [Fact]
    public void DecideWhenAuthOverlayHides_Phone_DoesNothing()
    {
        // Arrange
        const bool isTelevision = false;

        // Act
        var action = TvHeaderFocusGate.DecideWhenAuthOverlayHides(
            isTelevision, boardHasCards: false, boardSettledEmpty: false, streamExitPending: false);

        // Assert
        Assert.Equal(TvAuthOverlayHiddenAction.None, action);
    }

    [Fact]
    public void DecideOnGamesUpdated_RecordedRearmAndCardsLanded_RearmsAndRetries()
    {
        // Arrange - the overlay hid before any card existed (HoldUntilCards).
        const bool rearmOnNextCards = true;

        // Act
        var action = TvHeaderFocusGate.DecideOnGamesUpdated(
            isTelevision: true, boardHasCards: true, boardSettledEmpty: false, rearmOnNextCards, streamExitPending: false);

        // Assert
        Assert.Equal(TvGamesUpdatedAction.RearmAndRetry, action);
    }

    [Fact]
    public void DecideOnGamesUpdated_NoRecordedRearm_LeavesLaterCatalogsAlone()
    {
        // Arrange - a routine catalog refresh must not steal focus back to the first card.
        const bool rearmOnNextCards = false;

        // Act
        var action = TvHeaderFocusGate.DecideOnGamesUpdated(
            isTelevision: true, boardHasCards: true, boardSettledEmpty: false, rearmOnNextCards, streamExitPending: false);

        // Assert
        Assert.Equal(TvGamesUpdatedAction.None, action);
    }

    [Fact]
    public void DecideOnGamesUpdated_StreamExitPending_DoesNotRearm()
    {
        // Arrange - stream exit wins: the watched game keeps focus.
        const bool streamExitPending = true;

        // Act
        var action = TvHeaderFocusGate.DecideOnGamesUpdated(
            isTelevision: true, boardHasCards: true, boardSettledEmpty: false, rearmOnNextCards: true, streamExitPending);

        // Assert
        Assert.Equal(TvGamesUpdatedAction.None, action);
    }

    [Fact]
    public void DecideOnGamesUpdated_StillLoadingWithNoCards_KeepsWaiting()
    {
        // Arrange - e.g. the null/valve apply before the enriched catalog.
        const bool boardHasCards = false;

        // Act
        var action = TvHeaderFocusGate.DecideOnGamesUpdated(
            isTelevision: true, boardHasCards, boardSettledEmpty: false, rearmOnNextCards: true, streamExitPending: false);

        // Assert
        Assert.Equal(TvGamesUpdatedAction.None, action);
    }

    [Fact]
    public void DecideOnGamesUpdated_BoardSettledEmpty_Releases()
    {
        // Arrange
        const bool boardSettledEmpty = true;

        // Act
        var action = TvHeaderFocusGate.DecideOnGamesUpdated(
            isTelevision: true, boardHasCards: false, boardSettledEmpty, rearmOnNextCards: true, streamExitPending: false);

        // Assert
        Assert.Equal(TvGamesUpdatedAction.Release, action);
    }

    [Fact]
    public void ShouldAllowUpToMenu_BlockedOnlyWhileHoldHasNoCardAndNotTimedOut()
    {
        // Arrange
        const bool holdActive = true;

        // Act
        var allow = TvHeaderFocusGate.ShouldAllowUpToMenu(holdActive, anyCardHasHadFocus: false, holdTimedOut: false);

        // Assert
        Assert.False(allow);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void ShouldAllowUpToMenu_OnceCardFocusedOrTimedOutOrHoldReleased(
        bool holdActive, bool anyCardHasHadFocus, bool holdTimedOut)
    {
        // Arrange
        var active = holdActive;

        // Act
        var allow = TvHeaderFocusGate.ShouldAllowUpToMenu(active, anyCardHasHadFocus, holdTimedOut);

        // Assert
        Assert.True(allow);
    }

    [Fact]
    public void HoldTimeout_IsLongerThanTheFirstCardRetry()
    {
        // Arrange
        const long minimumMs = 5_000;

        // Act
        var timeout = TvHeaderFocusGate.HoldTimeoutMilliseconds;

        // Assert
        Assert.True(timeout >= minimumMs);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public void IsHoldTimedOut_UsesBoundedTimeout(long offsetFromTimeoutMs, bool expected)
    {
        // Arrange
        const long start = 1_000;
        var timeout = TvHeaderFocusGate.HoldTimeoutMilliseconds;

        // Act
        var timedOut = TvHeaderFocusGate.IsHoldTimedOut(start, start + timeout + offsetFromTimeoutMs, timeout);

        // Assert
        Assert.Equal(expected, timedOut);
    }
}
