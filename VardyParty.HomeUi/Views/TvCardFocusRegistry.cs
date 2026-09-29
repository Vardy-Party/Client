#if ANDROID
using AView = Android.Views.View;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// Live native card views keyed by <see cref="Presentation.HomeBoardDiffer.GameKey"/>.
/// Returning from the player focuses a fixture that may no longer be the
/// last focused view (the row was recycled, or the fixture left the board).
/// UI-thread only. Weak refs so a destroyed platform view is not kept alive.
/// </summary>
internal static class TvCardFocusRegistry
{
    private static readonly Dictionary<string, WeakReference<AView>> Cards = new(StringComparer.Ordinal);

    internal static void Move(string? previousKey, string gameKey, AView view)
    {
        if (!string.IsNullOrEmpty(previousKey)
            && !string.Equals(previousKey, gameKey, StringComparison.Ordinal)
            && Cards.TryGetValue(previousKey, out var previous)
            && previous.TryGetTarget(out var previousView)
            && ReferenceEquals(previousView, view))
        {
            Cards.Remove(previousKey);
        }

        Cards[gameKey] = new WeakReference<AView>(view);
    }

    internal static AView? TryGetAttached(string gameKey)
    {
        if (!Cards.TryGetValue(gameKey, out var weak))
        {
            return null;
        }

        if (weak.TryGetTarget(out var view))
        {
            return view.IsAttachedToWindow ? view : null;
        }

        Cards.Remove(gameKey);
        return null;
    }
}
#endif
