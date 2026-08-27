namespace VardyParty.HomeUi.Views;

/// <summary>
/// Netflix-style D-pad geometry for homepage rows whose cards sit in a
/// horizontal ScrollView + BindableLayout (not a nested RecyclerView).
/// The walk must stop on the BindableLayout host — unwrapping a one-card
/// row into the card's chrome made left/right edge clamps and column
/// memory see Grid children as sibling cards.
/// </summary>
public static class TvDpadStripWalk
{
    public interface INode
    {
        bool Focusable { get; }
        bool IsShown { get; }
        bool IsRecycler { get; }
        bool IsScroller { get; }
        int ChildCount { get; }
        INode? Parent { get; }
        INode? GetChild(int index);
        int Width { get; }
        int ScreenX { get; }
        bool RepresentsSame(INode? other);
    }

    public static INode? FindStripFromCard(INode card)
    {
        var scroller = FindAncestorScroller(card);
        return scroller is null ? null : FindCardStrip(scroller);
    }

    public static INode? FindAncestorScroller(INode node)
    {
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.IsRecycler)
            {
                return null;
            }

            if (parent.IsScroller)
            {
                return parent;
            }
        }

        return null;
    }

    public static INode? FindDescendantScroller(INode root)
    {
        if (root.IsScroller)
        {
            return root;
        }

        for (var i = 0; i < root.ChildCount; i++)
        {
            var child = root.GetChild(i);
            if (child is null)
            {
                continue;
            }

            if (FindDescendantScroller(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Unwrap scroller padding hosts until the BindableLayout stack (direct
    /// children are card items). Do not enter a card: a one-card row's stack
    /// has a single non-focusable MatchCardView whose inner Grid is chrome.
    /// </summary>
    public static INode? FindCardStrip(INode scroller)
    {
        var current = scroller;
        while (true)
        {
            if (IsBindableCardStrip(current))
            {
                return current;
            }

            if (current.ChildCount == 1
                && current.GetChild(0) is { } only
                && !only.Focusable)
            {
                current = only;
                continue;
            }

            return current.ChildCount >= 1 ? current : null;
        }
    }

    /// <summary>
    /// Every direct child is a card item, and a single child is not itself
    /// another strip (padding wrapper around the stack).
    /// </summary>
    public static bool IsBindableCardStrip(INode group)
    {
        if (group.ChildCount == 0 || !ChildrenAreAllCardItems(group))
        {
            return false;
        }

        if (group.ChildCount == 1
            && group.GetChild(0) is { } only
            && !only.Focusable
            && only.ChildCount > 1
            && ChildrenAreAllCardItems(only))
        {
            return false;
        }

        return true;
    }

    public static bool IsCardItem(INode node) =>
        (node.Focusable && node.IsShown) || HasFocusableDescendant(node);

    public static bool ChildrenAreAllCardItems(INode group)
    {
        if (group.ChildCount == 0)
        {
            return false;
        }

        for (var i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChild(i);
            if (child is null || !IsCardItem(child))
            {
                return false;
            }
        }

        return true;
    }

    public static bool HasFocusableDescendant(INode node)
    {
        for (var i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            if (child is null)
            {
                continue;
            }

            if (child.Focusable && child.IsShown)
            {
                return true;
            }

            if (HasFocusableDescendant(child))
            {
                return true;
            }
        }

        return false;
    }

    public static INode? FindDirectChild(INode parent, INode descendant)
    {
        for (var current = descendant; current != null; current = current.Parent)
        {
            if (current.Parent?.RepresentsSame(parent) == true)
            {
                return current;
            }
        }

        return null;
    }

    public static int IndexOfDirectChild(INode parent, INode child)
    {
        for (var i = 0; i < parent.ChildCount; i++)
        {
            if (parent.GetChild(i)?.RepresentsSame(child) == true)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Shown focusable leaves under a strip scroller. Card roots use
    /// BlockDescendants, so we stop at the first focusable and never treat
    /// inner chrome as sibling cards — including a one-card BindableLayout.
    /// </summary>
    public static void CollectShownFocusables(INode root, IList<INode> dest)
    {
        if (root.Focusable && root.IsShown)
        {
            dest.Add(root);
            return;
        }

        for (var i = 0; i < root.ChildCount; i++)
        {
            var child = root.GetChild(i);
            if (child is null)
            {
                continue;
            }

            CollectShownFocusables(child, dest);
        }
    }

    public static bool IsAtRowEdge(INode card, bool lastCard)
    {
        var scroller = FindAncestorScroller(card);
        if (scroller is null)
        {
            return false;
        }

        var cards = new List<INode>();
        CollectShownFocusables(scroller, cards);
        if (cards.Count == 0)
        {
            return false;
        }

        var index = -1;
        for (var i = 0; i < cards.Count; i++)
        {
            if (cards[i].RepresentsSame(card))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return false;
        }

        return lastCard ? index == cards.Count - 1 : index == 0;
    }

    public static INode? FindNearestFocusableByCenterX(INode root, int targetCenterX)
    {
        var searchRoot = root.IsScroller ? root : FindDescendantScroller(root) ?? root;
        var cards = new List<INode>();
        CollectShownFocusables(searchRoot, cards);

        INode? best = null;
        var bestDistance = int.MaxValue;
        foreach (var focusable in cards)
        {
            var distance = Math.Abs(CenterX(focusable) - targetCenterX);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = focusable;
            }
        }

        return best;
    }

    public static INode? FindFocusableDescendant(INode root)
    {
        if (root.Focusable && root.IsShown)
        {
            return root;
        }

        for (var i = 0; i < root.ChildCount; i++)
        {
            var child = root.GetChild(i);
            if (child is null)
            {
                continue;
            }

            if (FindFocusableDescendant(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public static int CenterX(INode view) => view.ScreenX + (view.Width / 2);

    /// <summary>
    /// Walk MAUI ancestors from a focused card: the horizontal strip scroller,
    /// the outer rows CollectionView, and the league-row BindingContext.
    /// Used to keep the focused card fully on screen (ring included).
    /// </summary>
    public static void IdentifyCatalogAncestors(
        IEnumerable<object> ancestors,
        out bool foundHorizontalStrip,
        out bool foundRowsCollection,
        out bool foundRowViewModel)
    {
        foundHorizontalStrip = false;
        foundRowsCollection = false;
        foundRowViewModel = false;

        foreach (var ancestor in ancestors)
        {
            if (ancestor is ScrollView { Orientation: ScrollOrientation.Horizontal })
            {
                foundHorizontalStrip = true;
            }

            if (ancestor is CollectionView)
            {
                foundRowsCollection = true;
            }

            if (ancestor is LeagueRowViewModel)
            {
                foundRowViewModel = true;
            }
        }
    }
}
