#if ANDROID
using AndroidX.RecyclerView.Widget;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// Netflix-style D-pad routing for the nested CollectionViews (RecyclerViews)
/// on Android TV. Android's default nearest-neighbour focus search judges the
/// clipped on-screen rectangles, so moving down often lands on whatever card
/// pokes out after scrolling — usually the start of the next row. This router
/// implements column memory instead: down/up lands on the card in the adjacent
/// row whose screen X is nearest the currently focused card, and left/right is
/// clamped at row edges so focus never leaps to a different row.
/// Everything here runs on the UI thread per key press with zero per-move
/// heap allocations (one shared scratch array for screen coordinates).
/// </summary>
internal static class TvDpadFocusRouter
{
    private static readonly int[] ScreenLocation = new int[2];

    /// <summary>
    /// Returns true when the key was fully handled (focus moved, or the move
    /// was clamped); false lets Android's default focus search run — which is
    /// still wanted for up from the first row (reaches the header Menu button)
    /// and for adjacent rows that are not laid out yet (the default
    /// focus-search-failed path scrolls them in).
    /// </summary>
    public static bool TryHandle(AView card, global::Android.Views.Keycode keyCode) => keyCode switch
    {
        global::Android.Views.Keycode.DpadDown => TryMoveVertical(card, down: true),
        global::Android.Views.Keycode.DpadUp => TryMoveVertical(card, down: false),
        global::Android.Views.Keycode.DpadLeft => IsAtRowEdge(card, lastCard: false),
        global::Android.Views.Keycode.DpadRight => IsAtRowEdge(card, lastCard: true),
        _ => false,
    };

    private static bool TryMoveVertical(AView card, bool down)
    {
        var strip = FindParentRecycler(card);
        var outer = strip is null ? null : FindParentRecycler(strip);
        if (strip is null || outer is null)
        {
            return false;
        }

        var rowItem = FindDirectChild(outer, strip);
        if (rowItem is null)
        {
            return false;
        }

        var rowPosition = outer.GetChildAdapterPosition(rowItem);
        if (rowPosition == RecyclerView.NoPosition)
        {
            return false;
        }

        var targetPosition = rowPosition + (down ? 1 : -1);
        if (targetPosition < 0)
        {
            // Above the first row: the default search must run so focus can
            // reach the header (Menu button).
            return false;
        }

        if (targetPosition >= (outer.GetAdapter()?.ItemCount ?? 0))
        {
            // Below the last row there is nothing; consume so focus stays put.
            return true;
        }

        var targetRow = outer.FindViewHolderForAdapterPosition(targetPosition)?.ItemView;
        var targetStrip = targetRow is null ? null : FindDescendantRecycler(targetRow);
        if (targetStrip is null)
        {
            // Adjacent row not laid out yet: fall back to the default search,
            // whose focus-search-failed path scrolls the outer list.
            return false;
        }

        var target = FindNearestFocusableByCenterX(targetStrip, CenterXOnScreen(card));
        return target?.RequestFocus() == true;
    }

    /// <summary>
    /// True when the card is the first (left) / last (right) of its strip:
    /// the key is consumed so the default search cannot jump focus to a
    /// neighbouring row's card that happens to sit past the row edge.
    /// </summary>
    private static bool IsAtRowEdge(AView card, bool lastCard)
    {
        var strip = FindParentRecycler(card);
        if (strip is null)
        {
            return false;
        }

        var item = FindDirectChild(strip, card);
        if (item is null)
        {
            return false;
        }

        var position = strip.GetChildAdapterPosition(item);
        if (position == RecyclerView.NoPosition)
        {
            return false;
        }

        return lastCard
            ? position == (strip.GetAdapter()?.ItemCount ?? 0) - 1
            : position == 0;
    }

    private static RecyclerView? FindParentRecycler(AView view)
    {
        for (var parent = view.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is RecyclerView recycler)
            {
                return recycler;
            }
        }

        return null;
    }

    /// <summary>The direct child of <paramref name="parent"/> containing <paramref name="descendant"/>.</summary>
    private static AView? FindDirectChild(RecyclerView parent, AView descendant)
    {
        for (var current = descendant; current != null; current = current.Parent as AView)
        {
            if (ReferenceEquals(current.Parent, parent))
            {
                return current;
            }
        }

        return null;
    }

    private static RecyclerView? FindDescendantRecycler(AView root)
    {
        if (root is RecyclerView recycler)
        {
            return recycler;
        }

        if (root is not AViewGroup group)
        {
            return null;
        }

        for (var i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChildAt(i);
            if (child is null)
            {
                continue;
            }

            if (FindDescendantRecycler(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static AView? FindNearestFocusableByCenterX(RecyclerView strip, int targetCenterX)
    {
        AView? best = null;
        var bestDistance = int.MaxValue;

        for (var i = 0; i < strip.ChildCount; i++)
        {
            var item = strip.GetChildAt(i);
            var focusable = item is null ? null : FindFocusableDescendant(item);
            if (focusable is null)
            {
                continue;
            }

            var distance = Math.Abs(CenterXOnScreen(focusable) - targetCenterX);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = focusable;
            }
        }

        return best;
    }

    private static AView? FindFocusableDescendant(AView root)
    {
        if (root.Focusable && root.IsShown)
        {
            return root;
        }

        if (root is not AViewGroup group)
        {
            return null;
        }

        for (var i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChildAt(i);
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

    private static int CenterXOnScreen(AView view)
    {
        view.GetLocationOnScreen(ScreenLocation);
        return ScreenLocation[0] + (view.Width / 2);
    }
}
#endif
