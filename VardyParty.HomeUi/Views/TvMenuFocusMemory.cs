namespace VardyParty.HomeUi.Views;

/// <summary>
/// Pure bookkeeping for the TV menu focus trap: remember which card had
/// focus when the menu opened, hand it back exactly once when the menu
/// closes, and clamp index moves inside the trapped item list. Platform-free
/// so the restore rules are unit-testable; the Android trap (HomeView.Tv.cs)
/// supplies opaque native views as the tokens and a liveness predicate at
/// close time (a recycled/detached card must not be restored).
/// </summary>
public sealed class TvMenuFocusMemory
{
    private object? _remembered;
    private bool _open;

    /// <summary>
    /// The trap opened over <paramref name="focusedBefore"/>. Re-entrant
    /// opens (menu key mashed while already open, an IsMenuOpen re-raise)
    /// keep the ORIGINAL pre-menu target — by the second call focus is
    /// already inside the menu, and remembering a menu item would restore
    /// focus into a panel that is about to disappear.
    /// </summary>
    public void OnTrapOpened(object? focusedBefore)
    {
        if (_open)
        {
            return;
        }

        _open = true;
        _remembered = focusedBefore;
    }

    /// <summary>
    /// The trap closed: returns the restore target exactly once, or null when
    /// there is nothing valid to restore (never opened, nothing was focused
    /// before, or <paramref name="isStillValid"/> rejects the remembered
    /// token — e.g. the card was recycled while the menu was open). The
    /// memory is cleared either way; a second close is a no-op.
    /// </summary>
    public object? OnTrapClosed(Func<object, bool>? isStillValid = null)
    {
        if (!_open)
        {
            return null;
        }

        _open = false;
        var remembered = _remembered;
        _remembered = null;
        if (remembered is null)
        {
            return null;
        }

        return isStillValid?.Invoke(remembered) != false ? remembered : null;
    }

    /// <summary>
    /// Index move inside the trapped item list: up/down step clamped at both
    /// ends (the trap consumes the key either way, so focus can never escape
    /// the panel). An index outside the list (stale collection) stays put.
    /// </summary>
    public static int MoveIndex(int index, int count, bool forward)
    {
        if (count <= 0 || index < 0 || index >= count)
        {
            return index;
        }

        return Math.Clamp(index + (forward ? 1 : -1), 0, count - 1);
    }
}
