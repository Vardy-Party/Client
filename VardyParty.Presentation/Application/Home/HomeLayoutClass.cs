namespace VardyParty.Presentation;

/// <summary>
/// Size/idiom class the homepage adapts to. One XAML tree, four layouts.
/// </summary>
public enum HomeLayoutClass
{
    Tv,
    Desktop,
    PhoneLandscape,
    PhonePortrait,
}

public static class HomeLayoutClassifier
{
    /// <summary>
    /// Shortest-side threshold (in device-independent pixels) below which a
    /// window is treated as a phone rather than a desktop/tablet surface.
    /// </summary>
    public const double PhoneShortestSideDip = 620;

    public static HomeLayoutClass Classify(double width, double height, bool isTelevision)
    {
        if (isTelevision) return HomeLayoutClass.Tv;
        if (width <= 0 || height <= 0) return HomeLayoutClass.Desktop;

        var shortestSide = Math.Min(width, height);
        if (shortestSide >= PhoneShortestSideDip) return HomeLayoutClass.Desktop;

        return height > width ? HomeLayoutClass.PhonePortrait : HomeLayoutClass.PhoneLandscape;
    }

    /// <summary>
    /// Layout class for a head's FIRST paint, decided synchronously at host
    /// construction — before any SizeChanged fires. The television flag wins
    /// outright; otherwise classify from the physical display size converted
    /// to device-independent pixels. Unknown display info (non-positive
    /// density) falls back to <see cref="Classify"/>'s unknown-size default
    /// (Desktop). Seeding exists so the first frame never renders one metrics
    /// class and then jumps to another — on Android TV that read as a startup
    /// "zoom" when the Tv class landed only after the first size event.
    /// </summary>
    public static HomeLayoutClass ClassifyInitial(
        bool isTelevision, double displayPixelWidth, double displayPixelHeight, double displayDensity)
    {
        if (isTelevision) return HomeLayoutClass.Tv;
        if (displayDensity <= 0) return Classify(0, 0, isTelevision: false);

        return Classify(
            displayPixelWidth / displayDensity,
            displayPixelHeight / displayDensity,
            isTelevision: false);
    }
}
