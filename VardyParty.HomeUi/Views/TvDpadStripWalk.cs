namespace VardyParty.HomeUi.Views;

/// <summary>
/// Netflix-style D-pad geometry for homepage rows whose cards sit in a
/// horizontal ScrollView + BindableLayout (not a nested RecyclerView).
/// <see cref="TvDpadFocusRouter"/> collects the shown focusable leaves under
/// a row's strip scroller and picks by nearest screen X; card roots use
/// BlockDescendants so inner chrome is never mistaken for sibling cards —
/// including in a one-card BindableLayout.
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

    public static int CenterX(INode view) => view.ScreenX + (view.Width / 2);
}
