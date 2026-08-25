namespace VardyParty.Presentation;

/// <summary>
/// Android TV homepage D-pad: autofocus the game grid once, then leave native
/// focus alone. Catalog refreshes must not steal the highlight back to the
/// first (or last-watched) card.
/// </summary>
public static class TvGridFocusPolicy
{
    /// <summary>
    /// Arm one programmatic autofocus when the grid first appears, or when the
    /// list shrinks past the last known card. Later BBC/API refreshes with a
    /// still-valid index must not re-arm.
    /// </summary>
    public static bool ShouldArmAutofocusOnCatalogRefresh(
        bool gridAlreadyShown,
        int focusedIndex,
        int displayedCount)
    {
        if (displayedCount <= 0)
            return false;

        if (!gridAlreadyShown)
            return true;

        return focusedIndex >= displayedCount;
    }

    public static int ClampFocusedIndex(int focusedIndex, int displayedCount)
    {
        if (displayedCount <= 0)
            return -1;

        if (focusedIndex < 0)
            return 0;

        if (focusedIndex >= displayedCount)
            return displayedCount - 1;

        return focusedIndex;
    }

    /// <summary>
    /// GameCard must FocusAsync once per ShouldFocus rising edge. Re-applying
    /// on every AfterRender snaps D-pad back to the armed card.
    /// </summary>
    public static bool ShouldDeliverProgrammaticFocus(bool shouldFocus, bool alreadyDelivered) =>
        shouldFocus && !alreadyDelivered;
}
