using System.Collections.Generic;
using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

/// <summary>
/// Pure-function coverage of the strip-walk geometry only. The FakeNode
/// trees here are simplified shapes, not the real MAUI/Android handler
/// tree (MauiScrollView / ContentViewGroup nesting, BlockDescendants
/// semantics, recycler wrapping): D-pad traversal against the real tree is
/// device-only coverage on Android TV hardware.
/// </summary>
public class TvDpadStripWalkTests
{
    [Fact]
    public void CollectShownFocusables_OneCardRow_IgnoresInnerChrome()
    {
        // Arrange — one-card row: scroller → padding → stack → card → grid chrome.
        var (scroller, _, cardOuter) = BuildRow(cardCount: 1, firstCardScreenX: 40);
        var cards = new List<TvDpadStripWalk.INode>();

        // Act
        TvDpadStripWalk.CollectShownFocusables(scroller, cards);
        var atLeft = TvDpadStripWalk.IsAtRowEdge(cardOuter, lastCard: false);
        var atRight = TvDpadStripWalk.IsAtRowEdge(cardOuter, lastCard: true);

        // Assert
        Assert.Single(cards);
        Assert.True(cardOuter.RepresentsSame(cards[0]));
        Assert.True(atLeft);
        Assert.True(atRight);
    }

    [Fact]
    public void CollectShownFocusables_MultiCardRow_OneLeafPerCard()
    {
        // Arrange
        var (scroller, _, _) = BuildRow(cardCount: 3, firstCardScreenX: 0);
        var cards = new List<TvDpadStripWalk.INode>();

        // Act
        TvDpadStripWalk.CollectShownFocusables(scroller, cards);

        // Assert
        Assert.Equal(3, cards.Count);
    }

    [Fact]
    public void ColumnMemory_PicksNearestCenterX_NotFirstCard()
    {
        // Arrange — adjacent row scrolled so card 0 is off-screen to the left.
        var (targetScroller, _, _) = BuildRow(cardCount: 3, firstCardScreenX: -200);
        const int focusedCenterX = 170; // card 0 in the unshifted row (x=0, w=340)

        // Act
        var nearest = TvDpadStripWalk.FindNearestFocusableByCenterX(targetScroller, focusedCenterX);

        // Assert
        Assert.NotNull(nearest);
        Assert.Equal(-200 + 356, nearest!.ScreenX);
    }

    [Fact]
    public void IsAtRowEdge_MiddleCard_DoesNotClamp()
    {
        // Arrange
        var (scroller, _, _) = BuildRow(cardCount: 3, firstCardScreenX: 0);
        var cards = new List<TvDpadStripWalk.INode>();
        TvDpadStripWalk.CollectShownFocusables(scroller, cards);
        var middle = cards[1];

        // Act
        var atLeft = TvDpadStripWalk.IsAtRowEdge(middle, lastCard: false);
        var atRight = TvDpadStripWalk.IsAtRowEdge(middle, lastCard: true);

        // Assert
        Assert.False(atLeft);
        Assert.False(atRight);
    }

    [Fact]
    public void FindAdjacentInRow_MiddleCard_ReturnsBothNeighbours()
    {
        // Arrange
        var (scroller, _, _) = BuildRow(cardCount: 3, firstCardScreenX: 0);
        var cards = new List<TvDpadStripWalk.INode>();
        TvDpadStripWalk.CollectShownFocusables(scroller, cards);
        var middle = cards[1];

        // Act
        var previous = TvDpadStripWalk.FindAdjacentInRow(middle, forward: false);
        var next = TvDpadStripWalk.FindAdjacentInRow(middle, forward: true);

        // Assert
        Assert.True(cards[0].RepresentsSame(previous));
        Assert.True(cards[2].RepresentsSame(next));
    }

    [Fact]
    public void FindAdjacentInRow_EdgeCards_ReturnNullPastTheEdge()
    {
        // Arrange
        var (scroller, _, firstCard) = BuildRow(cardCount: 3, firstCardScreenX: 0);
        var cards = new List<TvDpadStripWalk.INode>();
        TvDpadStripWalk.CollectShownFocusables(scroller, cards);
        var lastCard = cards[2];

        // Act
        var beforeFirst = TvDpadStripWalk.FindAdjacentInRow(firstCard, forward: false);
        var afterLast = TvDpadStripWalk.FindAdjacentInRow(lastCard, forward: true);

        // Assert
        Assert.Null(beforeFirst);
        Assert.Null(afterLast);
    }

    [Fact]
    public void FindAdjacentInRow_CardOutsideAnyStrip_ReturnsNull()
    {
        // Arrange — a focusable with no ancestor scroller (e.g. a header control).
        var orphan = new FakeNode { Focusable = true, IsShown = true, Width = 120 };

        // Act
        var neighbour = TvDpadStripWalk.FindAdjacentInRow(orphan, forward: true);

        // Assert
        Assert.Null(neighbour);
    }

    private static (FakeNode Scroller, FakeNode Stack, FakeNode FirstCardOuter) BuildRow(
        int cardCount,
        int firstCardScreenX)
    {
        var scroller = new FakeNode { IsScroller = true };
        var padding = new FakeNode();
        var stack = new FakeNode();
        scroller.Add(padding);
        padding.Add(stack);

        FakeNode? firstOuter = null;
        for (var i = 0; i < cardCount; i++)
        {
            var card = new FakeNode();
            var grid = new FakeNode();
            var chrome = new FakeNode();
            var outer = new FakeNode
            {
                Focusable = true,
                IsShown = true,
                Width = 340,
                ScreenX = firstCardScreenX + (i * 356),
            };
            card.Add(grid);
            grid.Add(chrome);
            grid.Add(outer);
            stack.Add(card);
            firstOuter ??= outer;
        }

        return (scroller, stack, firstOuter!);
    }

    private sealed class FakeNode : TvDpadStripWalk.INode
    {
        private readonly List<FakeNode> _children = new();

        public bool Focusable { get; init; }
        public bool IsShown { get; init; } = true;
        public bool IsRecycler { get; init; }
        public bool IsScroller { get; init; }
        public int ChildCount => _children.Count;
        public TvDpadStripWalk.INode? Parent { get; private set; }
        public int Width { get; init; }
        public int ScreenX { get; init; }

        public TvDpadStripWalk.INode? GetChild(int index) =>
            index >= 0 && index < _children.Count ? _children[index] : null;

        public bool RepresentsSame(TvDpadStripWalk.INode? other) => ReferenceEquals(this, other);

        public void Add(FakeNode child)
        {
            child.Parent = this;
            _children.Add(child);
        }
    }
}
