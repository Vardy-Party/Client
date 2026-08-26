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
}
