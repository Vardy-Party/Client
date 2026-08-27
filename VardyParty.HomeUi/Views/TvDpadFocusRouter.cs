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
    /// Frames the deferred focus of a not-yet-attached row keeps retrying
    /// while the recycler smooth-scrolls it in (~1.5s at 60fps; the 32-bit
    /// TV box drops frames, and each retry is one cheap looper post).
    /// </summary>
    private const int RowAttachRetryFrames = 90;

    private static bool _ownsRowReveal;

    /// <summary>
    /// One-shot flag: the router revealed the target row itself for the
    /// focus move currently being delivered. <see cref="MatchCardView"/>
    /// consumes it to skip its own MAUI rows ScrollTo — a second, item-based
    /// smooth scroll would cancel the router's in-flight animation mid-move
    /// (the vertical flavour of the two-scroll-owners bug). UI thread only.
    /// </summary>
    internal static bool TryConsumeOwnedRowReveal()
    {
        var owned = _ownsRowReveal;
        _ownsRowReveal = false;
        return owned;
    }

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
            // The adjacent row is not attached yet. Previously this fell
            // through to Android's focus-search-failed path, which scrolled
            // the row in with an abrupt layout-time jump (and played the
            // system navigation click, see OnNativeKeyPress). Own it
            // instead: one animated recycler scroll, then land focus with
            // column memory once the row attaches.
            outer.SmoothScrollToPosition(targetPosition);
            FocusRowWhenAttached(outer, targetPosition, CenterXOnScreen(card), RowAttachRetryFrames);
            return true;
        }

        var scroller = TvDpadStripWalk.FindDescendantScroller(Wrap(targetRow));
        if (scroller is null)
        {
            return false;
        }

        var target = TvDpadStripWalk.FindNearestFocusableByCenterX(scroller, CenterXOnScreen(card));
        if (target is not AndroidNode node)
        {
            return false;
        }

        // Reveal the row OURSELVES before moving focus: with a smooth scroll
        // in flight, RecyclerView.LayoutManager.onRequestChildFocus reports
        // isSmoothScrolling() and the framework skips its own
        // requestChildOnScreen (which ignores revealOnFocusHint and reveals
        // only the card rect, leaving the league header clipped on upward
        // moves). One scroll owner per axis, same rule as the strips.
        var delta = TvFocusScrollMath.ComputeVerticalRevealDelta(
            targetRow.Top, targetRow.Bottom, outer.Height);
        if (delta != 0)
        {
            outer.SmoothScrollBy(0, delta);
        }

        _ownsRowReveal = true;
        if (node.View.RequestFocus())
        {
            return true;
        }

        _ownsRowReveal = false;
        return delta != 0;
    }

    /// <summary>
    /// Deferred column-memory focus for a row the recycler is still smooth-
    /// scrolling in: retry once per frame until the row attaches and lays
    /// out, then focus its nearest card. Focus landing mid-scroll is safe —
    /// the in-flight smooth scroll suppresses the framework's own reveal.
    /// </summary>
    private static void FocusRowWhenAttached(
        RecyclerView outer, int targetPosition, int centerX, int attemptsLeft)
    {
        if (attemptsLeft <= 0 || !outer.IsAttachedToWindow)
        {
            return;
        }

        var targetRow = outer.FindViewHolderForAdapterPosition(targetPosition)?.ItemView;
        if (targetRow is null || targetRow.Width == 0)
        {
            outer.Post(() => FocusRowWhenAttached(outer, targetPosition, centerX, attemptsLeft - 1));
            return;
        }

        var target = TvDpadStripWalk.FindNearestFocusableByCenterX(Wrap(targetRow), centerX);
        if (target is AndroidNode node)
        {
            _ownsRowReveal = true;
            if (!node.View.RequestFocus())
            {
                _ownsRowReveal = false;
            }
        }
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
