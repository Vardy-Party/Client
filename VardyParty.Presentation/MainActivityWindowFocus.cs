namespace VardyParty.Presentation;

/// <summary>
/// Leanback opens MainActivity through TvLeanbackAlias before the window
/// has focus. A focus request then is ignored, so the first D-pad OK is
/// dropped. Ask only once <see cref="ShouldRequestFocus"/> is true: the
/// window has focus, and the current focus is nothing or the decor view.
/// A Sign in control that already has focus is left alone, including when
/// the window regains focus after the native video activity.
/// </summary>
public static class MainActivityWindowFocus
{
    public static bool ShouldRequestFocus(bool hasWindowFocus, bool currentFocusIsNullOrDecor) =>
        hasWindowFocus && currentFocusIsNullOrDecor;
}
