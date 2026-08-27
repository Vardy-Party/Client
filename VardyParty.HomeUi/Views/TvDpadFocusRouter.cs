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
/// is shared XAML — this router walks that tree via
/// <see cref="TvDpadStripWalk"/> instead of assuming two RecyclerViews.
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
        global::Android.Views.Keycode.DpadLeft => TvDpadStripWalk.IsAtRowEdge(Wrap(card), lastCard: false),
        global::Android.Views.Keycode.DpadRight => TvDpadStripWalk.IsAtRowEdge(Wrap(card), lastCard: true),
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
        if (targetRow is null)
        {
            return false;
        }

        var scroller = TvDpadStripWalk.FindDescendantScroller(Wrap(targetRow));
        var strip = scroller is null ? null : TvDpadStripWalk.FindCardStrip(scroller);
        if (strip is null)
        {
            return false;
        }

        var target = TvDpadStripWalk.FindNearestFocusableByCenterX(strip, CenterXOnScreen(card));
        return target is AndroidNode node && node.View.RequestFocus();
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

    private static int CenterXOnScreen(AView view)
    {
        view.GetLocationOnScreen(ScreenLocation);
        return ScreenLocation[0] + (view.Width / 2);
    }

    private static AndroidNode Wrap(AView view) => new(view);

    /// <summary>
    /// MAUI's horizontal ScrollView is a platform scroller (MauiScrollView /
    /// HorizontalScrollView / NestedScrollView), not a RecyclerView.
    /// </summary>
    internal static bool IsPlatformScroller(AView view)
    {
        if (view is RecyclerView)
        {
            return false;
        }

        if (view is HorizontalScrollView or AndroidX.Core.Widget.NestedScrollView)
        {
            return true;
        }

        var name = view.Class?.SimpleName;
        return name is not null && name.Contains("ScrollView", StringComparison.Ordinal);
    }

    private sealed class AndroidNode : TvDpadStripWalk.INode
    {
        public AndroidNode(AView view) => View = view;

        public AView View { get; }

        public bool Focusable => View.Focusable;

        public bool IsShown => View.IsShown;

        public bool IsRecycler => View is RecyclerView;

        public bool IsScroller => IsPlatformScroller(View);

        public int ChildCount => View is AViewGroup group ? group.ChildCount : 0;

        public TvDpadStripWalk.INode? Parent =>
            View.Parent is AView parentView ? new AndroidNode(parentView) : null;

        public TvDpadStripWalk.INode? GetChild(int index)
        {
            if (View is not AViewGroup group)
            {
                return null;
            }

            var child = group.GetChildAt(index);
            return child is null ? null : new AndroidNode(child);
        }

        public int Width => View.Width;

        public int ScreenX
        {
            get
            {
                View.GetLocationOnScreen(ScreenLocation);
                return ScreenLocation[0];
            }
        }

        public bool RepresentsSame(TvDpadStripWalk.INode? other) =>
            other is AndroidNode node && ReferenceEquals(View, node.View);
    }
}
#endif
