using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

/// <summary>
/// Restore bookkeeping for the TV menu focus trap: the card focused before
/// the menu opened comes back exactly once on close, stale/recycled cards are
/// rejected, and index moves clamp inside the trapped list.
/// </summary>
public class TvMenuFocusMemoryTests
{
    private readonly TvMenuFocusMemory _sut = new();

    [Fact]
    public void Close_ReturnsThePreMenuFocusTarget()
    {
        var card = new object();

        _sut.OnTrapOpened(card);

        Assert.Same(card, _sut.OnTrapClosed());
    }

    [Fact]
    public void Close_ReturnsTheTargetOnlyOnce()
    {
        _sut.OnTrapOpened(new object());
        _sut.OnTrapClosed();

        Assert.Null(_sut.OnTrapClosed());
    }

    [Fact]
    public void Close_WithoutOpen_ReturnsNull()
    {
        Assert.Null(_sut.OnTrapClosed());
    }

    [Fact]
    public void Open_WithNothingFocused_ClosesToNull()
    {
        _sut.OnTrapOpened(null);

        Assert.Null(_sut.OnTrapClosed());
    }

    [Fact]
    public void ReentrantOpen_KeepsTheOriginalPreMenuTarget()
    {
        var card = new object();
        var menuItem = new object();

        _sut.OnTrapOpened(card);
        // Second open while trapped: focus is already on a menu item by now.
        _sut.OnTrapOpened(menuItem);

        Assert.Same(card, _sut.OnTrapClosed());
    }

    [Fact]
    public void Close_RejectsATargetTheValidityCheckDeclaresStale()
    {
        _sut.OnTrapOpened(new object());

        Assert.Null(_sut.OnTrapClosed(_ => false));
    }

    [Fact]
    public void Close_KeepsATargetTheValidityCheckAccepts()
    {
        var card = new object();
        _sut.OnTrapOpened(card);

        Assert.Same(card, _sut.OnTrapClosed(_ => true));
    }

    [Fact]
    public void Close_AfterRejectedRestore_StaysCleared()
    {
        _sut.OnTrapOpened(new object());
        _sut.OnTrapClosed(_ => false);

        Assert.Null(_sut.OnTrapClosed());
    }

    [Theory]
    [InlineData(0, 5, true, 1)]   // down moves forward
    [InlineData(1, 5, false, 0)]  // up moves back
    [InlineData(4, 5, true, 4)]   // clamped at the last item
    [InlineData(0, 5, false, 0)]  // clamped at the first item
    [InlineData(-1, 5, true, -1)] // unknown item stays put
    [InlineData(5, 5, true, 5)]   // stale index stays put
    [InlineData(0, 0, true, 0)]   // empty list stays put
    public void MoveIndex_StepsAndClamps(int index, int count, bool forward, int expected)
    {
        Assert.Equal(expected, TvMenuFocusMemory.MoveIndex(index, count, forward));
    }
}
