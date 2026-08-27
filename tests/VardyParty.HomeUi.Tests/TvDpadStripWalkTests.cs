using System.Collections.Generic;
using Microsoft.Maui.Controls;
using VardyParty.HomeUi;
using VardyParty.HomeUi.Views;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public class TvDpadStripWalkTests
{
    [Fact]
    public void FindCardStrip_StopsOnBindableLayout_NotInsideOneCardChrome()
    {
        // Arrange — one-card row: scroller → padding → stack → card → grid chrome.
        var (scroller, stack, cardOuter) = BuildRow(cardCount: 1, firstCardScreenX: 40);

        // Act
        var strip = TvDpadStripWalk.FindCardStrip(scroller);
        var atLeft = TvDpadStripWalk.IsAtRowEdge(cardOuter, lastCard: false);
        var atRight = TvDpadStripWalk.IsAtRowEdge(cardOuter, lastCard: true);

        // Assert
        Assert.Same(stack, strip);
        Assert.Equal(1, strip!.ChildCount);
        Assert.True(atLeft);
        Assert.True(atRight);
    }

    [Fact]
    public void FindCardStrip_MultiCardRow_DoesNotUnwrapIntoFirstCard()
    {
        // Arrange
        var (scroller, stack, _) = BuildRow(cardCount: 3, firstCardScreenX: 0);

        // Act
        var strip = TvDpadStripWalk.FindCardStrip(scroller);

        // Assert
        Assert.Same(stack, strip);
        Assert.Equal(3, strip!.ChildCount);
    }

    [Fact]
    public void ColumnMemory_PicksNearestCenterX_NotFirstCard()
    {
        // Arrange — adjacent row scrolled so card 0 is off-screen to the left.
        var (targetScroller, _, _) = BuildRow(cardCount: 3, firstCardScreenX: -200);
        var strip = TvDpadStripWalk.FindCardStrip(targetScroller);
        const int focusedCenterX = 170; // card 0 in the unshifted row (x=0, w=340)

        // Act
        var nearest = TvDpadStripWalk.FindNearestFocusableByCenterX(strip!, focusedCenterX);

        // Assert
        Assert.NotNull(nearest);
        Assert.Equal(-200 + 356, nearest!.ScreenX);
    }

    [Fact]
    public void IsAtRowEdge_MiddleCard_DoesNotClamp()
    {
        // Arrange
        var (_, _, first) = BuildRow(cardCount: 3, firstCardScreenX: 0);
        var stack = first.Parent!.Parent!.Parent!;
        var middle = TvDpadStripWalk.FindFocusableDescendant(stack.GetChild(1)!)!;

        // Act
        var atLeft = TvDpadStripWalk.IsAtRowEdge(middle, lastCard: false);
        var atRight = TvDpadStripWalk.IsAtRowEdge(middle, lastCard: true);

        // Assert
        Assert.False(atLeft);
        Assert.False(atRight);
    }

    [Fact]
    public void IdentifyCatalogAncestors_SeesStripAndRows()
    {
        // Arrange
        var ancestors = new List<object>
        {
            new ScrollView { Orientation = ScrollOrientation.Horizontal },
            new LeagueRowViewModel("League Alpha", false, new List<MatchCardViewModel>(), new HomeLayoutState()),
            new CollectionView(),
        };

        // Act
        TvDpadStripWalk.IdentifyCatalogAncestors(
            ancestors,
            out var strip,
            out var rows,
            out var rowVm);

        // Assert
        Assert.True(strip);
        Assert.True(rows);
        Assert.True(rowVm);
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
