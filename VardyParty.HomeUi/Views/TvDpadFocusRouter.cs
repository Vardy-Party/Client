#if ANDROID
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// Netflix-style D-pad routing for the homepage catalog on Android TV.
/// Rows are a vertical <see cref="RecyclerView"/> (MAUI CollectionView);
/// each row's cards are a horizontal ScrollView + BindableLayout, not a
/// nested RecyclerView. Nested ItemsRepeaters crash WinUI, so the strip
/// is shared XAML — this router walks that tree instead of assuming two
/// RecyclerViews.
/// Down/up land on the card in the adjacent row whose screen X is nearest
/// the focused card. Left/right at a row edge are consumed so focus cannot
/// leap to another row. UI-thread only; one scratch array for coordinates.
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
        var outer = FindParentRecycler(card);
        if (outer is null)
        {
            return false;
        }

        var rowItem = FindDirectChild(outer, card);
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
            return false;
        }

        if (targetPosition >= (outer.GetAdapter()?.ItemCount ?? 0))
        {
            return true;
        }

        var targetRow = outer.FindViewHolderForAdapterPosition(targetPosition)?.ItemView;
        var targetStrip = targetRow is null ? null : FindStripInRow(targetRow);
        if (targetStrip is null)
        {
            return false;
        }

        var target = FindNearestFocusableByCenterX(targetStrip, CenterXOnScreen(card));
        return target?.RequestFocus() == true;
    }

    private static bool IsAtRowEdge(AView card, bool lastCard)
    {
        var strip = FindStrip(card);
        if (strip is null)
        {
            return false;
        }

        var item = FindDirectChild(strip, card);
        if (item is null)
        {
            return false;
        }

        var position = IndexOfDirectChild(strip, item);
        if (position < 0)
        {
            return false;
        }

        return lastCard
            ? position == strip.ChildCount - 1
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

    private static AViewGroup? FindStrip(AView card)
    {
        for (var parent = card.Parent as AView; parent != null; parent = parent.Parent as AView)
        {
            if (parent is RecyclerView)
            {
                return null;
            }

            if (IsScroller(parent) && parent is AViewGroup scroller)
            {
                return ContentHost(scroller);
            }
        }

        return null;
    }

    private static AViewGroup? FindStripInRow(AView row)
    {
        var scroller = FindDescendantScroller(row);
        return scroller is AViewGroup group ? ContentHost(group) : null;
    }

    private static AView? FindDescendantScroller(AView root)
    {
        if (IsScroller(root))
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

            if (FindDescendantScroller(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// MAUI's horizontal ScrollView is a platform scroller (MauiScrollView /
    /// HorizontalScrollView / NestedScrollView), not a RecyclerView.
    /// </summary>
    private static bool IsScroller(AView view)
    {
        if (view is RecyclerView)
        {
            return false;
        }

        if (view is HorizontalScrollView)
        {
            return true;
        }

        var name = view.Class?.SimpleName;
        return name is not null && name.Contains("ScrollView", StringComparison.Ordinal);
    }

    /// <summary>
    /// Unwrap single-child scroller padding hosts until the BindableLayout
    /// stack (one child per card) is reached. Do not descend into a focusable
    /// card root — a one-card row would otherwise treat chrome as siblings.
    /// </summary>
    private static AViewGroup? ContentHost(AViewGroup scroller)
    {
        var current = scroller;
        while (current.ChildCount == 1 && current.GetChildAt(0) is AViewGroup only)
        {
            if (only.Focusable)
            {
                break;
            }

            current = only;
        }

        return current.ChildCount >= 1 ? current : null;
    }

    private static AView? FindDirectChild(AViewGroup parent, AView descendant)
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

    private static int IndexOfDirectChild(AViewGroup parent, AView child)
    {
        for (var i = 0; i < parent.ChildCount; i++)
        {
            if (ReferenceEquals(parent.GetChildAt(i), child))
            {
                return i;
            }
        }

        return -1;
    }

    private static AView? FindNearestFocusableByCenterX(AViewGroup strip, int targetCenterX)
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
