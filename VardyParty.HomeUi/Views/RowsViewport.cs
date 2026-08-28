namespace VardyParty.HomeUi.Views;

/// <summary>
/// CollectionView on every MAUI backend (WinUI ItemsRepeater, Android
/// RecyclerView, Avalonia virtualizing stack) will <c>Fill</c> its grid
/// cell but size the INNER scroll viewport to a content estimate. The
/// leftover cell paints the CollectionView's default black background —
/// the field "big black rectangle covering the bottom of the screen" on
/// Windows, Linux and Android TV — and clips any row that scrolls into
/// that band. Pinning <c>HeightRequest</c> to the host's arranged height
/// forces the inner viewport to the real leftover space.
/// </summary>
public static class RowsViewport
{
    /// <summary>
    /// Next HeightRequest for the rows list, or null when the host has no
    /// size yet or the request is already in place (avoids a layout loop).
    /// </summary>
    public static double? HeightRequest(double hostHeight, double currentRequest)
    {
        if (hostHeight <= 0)
        {
            return null;
        }

        if (currentRequest > 0 && Math.Abs(currentRequest - hostHeight) < 0.5)
        {
            return null;
        }

        return hostHeight;
    }
}
