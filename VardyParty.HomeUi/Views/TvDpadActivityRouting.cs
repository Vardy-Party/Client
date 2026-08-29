namespace VardyParty.HomeUi.Views;

/// <summary>
/// Pure decision table for the ACTIVITY-level D-pad ownership on Android TV
/// (no platform types; unit tested headlessly).
///
/// WHY THE ACTIVITY, AND WHY Activity.DispatchKeyEvent SPECIFICALLY:
/// per-card key listeners lived on platform views that RecyclerView
/// recycling and staged strip appends could detach/re-attach without any
/// re-wiring event — the recurring per-card-wiring gap. The activity is the
/// one dispatch point no materialization path can lose. And it must be
/// <c>DispatchKeyEvent</c> (which runs BEFORE the view tree), not
/// <c>OnKeyDown</c> (which runs after the view tree declines): the strips
/// are HorizontalScrollViews, whose <c>dispatchKeyEvent</c> override runs
/// <c>executeKeyEvent → arrowScroll</c> for LEFT/RIGHT whenever the focused
/// descendant declines the key — a hidden third scroll owner that scrolls
/// layout-rect-only (chrome clipped) and races our animated chrome-padded
/// scroll. Below the scrollers, ViewRootImpl.performFocusNavigation is the
/// final fallback, which plays the system navigation click unconditionally
/// and instant-reveals the target. Owning the key before the view tree
/// eliminates both by construction, on every rail, however a card was
/// materialized.
/// </summary>
public static class TvDpadActivityRouting
{
    public enum Decision
    {
        /// <summary>Not our key or not our surface: let Android proceed
        /// (this is also how the open menu trap's per-item listeners get
        /// their turn — they live in the view tree).</summary>
        NotHandled,

        /// <summary>
        /// The menu focus trap is open but focus is stranded OUTSIDE the
        /// panel (a card or the header): consume. Direction keys must never
        /// move focus behind the scrim.
        /// </summary>
        SealMenuTrap,

        /// <summary>
        /// Focus is on the header Menu button: down returns to the last
        /// focused card (column memory), every other direction is consumed —
        /// the header is a single focus stop and the crest stays skipped.
        /// </summary>
        HeaderMove,

        /// <summary>
        /// Focus is on a card (inside the rows recycler): route through
        /// <c>TvDpadFocusRouter.TryHandle</c> and consume the key whether or
        /// not a move happened. Neither the strip scroller's arrowScroll nor
        /// Android's default focus search may ever run for card navigation.
        /// </summary>
        RouteCard,
    }

    /// <summary>
    /// Dispatch-stage decision (Activity.DispatchKeyEvent, before the view
    /// tree sees the key).
    /// </summary>
    public static Decision Decide(
        bool isTelevision,
        bool isDirectionKey,
        bool menuTrapOpen,
        bool focusIsHeader,
        bool focusInsideRows)
    {
        if (!isTelevision || !isDirectionKey || !(focusIsHeader || focusInsideRows))
        {
            return Decision.NotHandled;
        }

        if (menuTrapOpen)
        {
            return Decision.SealMenuTrap;
        }

        return focusIsHeader ? Decision.HeaderMove : Decision.RouteCard;
    }

    /// <summary>
    /// Fallback-stage decision (Activity.OnKeyDown, after the whole view
    /// tree declined the key): with the menu trap open, a direction key that
    /// no trap item consumed means the per-item wiring was bypassed — it is
    /// swallowed so ViewRootImpl's focus search can never carry focus out of
    /// the panel to a card behind the scrim.
    /// </summary>
    public static bool SealsTrapFallback(bool isTelevision, bool isDirectionKey, bool menuTrapOpen) =>
        isTelevision && isDirectionKey && menuTrapOpen;
}
