using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

/// <summary>
/// Decision table for the activity-level TV D-pad owner. These are the
/// stage decisions only (pure); the Android glue that maps a decision to
/// RequestFocus/consume is device-only coverage.
/// </summary>
public class TvDpadActivityRoutingTests
{
    [Fact]
    public void Decide_CardFocused_RoutesCard()
    {
        // Arrange
        const bool focusInsideRows = true;

        // Act
        var decision = TvDpadActivityRouting.Decide(
            isTelevision: true, isDirectionKey: true, menuTrapOpen: false,
            focusIsHeader: false, focusInsideRows: focusInsideRows);

        // Assert
        Assert.Equal(TvDpadActivityRouting.Decision.RouteCard, decision);
    }

    [Fact]
    public void Decide_HeaderFocused_OwnsHeaderMove()
    {
        // Arrange
        const bool focusIsHeader = true;

        // Act
        var decision = TvDpadActivityRouting.Decide(
            isTelevision: true, isDirectionKey: true, menuTrapOpen: false,
            focusIsHeader: focusIsHeader, focusInsideRows: false);

        // Assert
        Assert.Equal(TvDpadActivityRouting.Decision.HeaderMove, decision);
    }

    [Fact]
    public void Decide_TrapOpenWithFocusStrandedOnCard_Seals()
    {
        // Arrange — the menu just opened but focus has not landed in the
        // panel yet: a direction key must not move focus behind the scrim.
        const bool menuTrapOpen = true;

        // Act
        var decision = TvDpadActivityRouting.Decide(
            isTelevision: true, isDirectionKey: true, menuTrapOpen: menuTrapOpen,
            focusIsHeader: false, focusInsideRows: true);

        // Assert
        Assert.Equal(TvDpadActivityRouting.Decision.SealMenuTrap, decision);
    }

    [Fact]
    public void Decide_TrapOpenWithFocusInPanel_PassesToTrapItems()
    {
        // Arrange — panel items own their moves at the view level; the
        // dispatch stage must not starve their key listeners.
        const bool menuTrapOpen = true;

        // Act
        var decision = TvDpadActivityRouting.Decide(
            isTelevision: true, isDirectionKey: true, menuTrapOpen: menuTrapOpen,
            focusIsHeader: false, focusInsideRows: false);

        // Assert
        Assert.Equal(TvDpadActivityRouting.Decision.NotHandled, decision);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Decide_NotTvOrNotDirection_NeverHandles(bool isTelevision, bool isDirectionKey)
    {
        // Arrange — every focus context at once: the gate must win.
        const bool menuTrapOpen = true;

        // Act
        var decision = TvDpadActivityRouting.Decide(
            isTelevision, isDirectionKey, menuTrapOpen,
            focusIsHeader: true, focusInsideRows: true);

        // Assert
        Assert.Equal(TvDpadActivityRouting.Decision.NotHandled, decision);
    }

    [Fact]
    public void Decide_FocusOutsideBoardAndHeader_LeavesKeyToAndroid()
    {
        // Arrange — e.g. a sign-in overlay control has focus.
        const bool focusInsideRows = false;

        // Act
        var decision = TvDpadActivityRouting.Decide(
            isTelevision: true, isDirectionKey: true, menuTrapOpen: false,
            focusIsHeader: false, focusInsideRows: focusInsideRows);

        // Assert
        Assert.Equal(TvDpadActivityRouting.Decision.NotHandled, decision);
    }

    [Fact]
    public void SealsTrapFallback_TrapOpenDirectionKeyOnTv_Seals()
    {
        // Arrange
        const bool menuTrapOpen = true;

        // Act
        var seals = TvDpadActivityRouting.SealsTrapFallback(
            isTelevision: true, isDirectionKey: true, menuTrapOpen: menuTrapOpen);

        // Assert
        Assert.True(seals);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void SealsTrapFallback_AnyGateMissing_DoesNotSeal(
        bool isTelevision, bool isDirectionKey, bool menuTrapOpen)
    {
        // Arrange — inputs cover each gate individually.

        // Act
        var seals = TvDpadActivityRouting.SealsTrapFallback(isTelevision, isDirectionKey, menuTrapOpen);

        // Assert
        Assert.False(seals);
    }
}
