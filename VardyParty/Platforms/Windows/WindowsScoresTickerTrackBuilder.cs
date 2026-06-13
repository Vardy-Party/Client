using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VardyParty.Services;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinImage = Microsoft.UI.Xaml.Controls.Image;
using WinRect = global::Windows.Foundation.Rect;
using WinSolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using WinStretch = Microsoft.UI.Xaml.Media.Stretch;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using WinSize = global::Windows.Foundation.Size;

namespace VardyParty.Platforms.Windows;

internal static class WindowsScoresTickerTrackBuilder
{
    private const double FlagWidth = 22;
    private const double FlagHeight = 15;
    private const double FontSize = 15;
    private const double LineHeight = 18;
    // WinUI TextBlock trims trailing spaces; use margin for symmetric separator padding.
    private const double SeparatorSidePadding = 8;
    private const double FlagRightMargin = 6;
    private const double LeagueToTeamGap = 10;
    private const double ScoreHorizontalMargin = 10;
    private const double StatusLeftMargin = 10;
    private const double VsHorizontalMargin = 8;
    private const double TeamNameRightMargin = 6;
    private const double HeaderRightMargin = 10;
    private const double KickoffTimeRightMargin = 10;

    public static StackPanel CreateTrack()
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = WinVerticalAlignment.Center,
            RenderTransform = new TranslateTransform()
        };
    }

    public static void RebuildTrack(StackPanel track, IReadOnlyList<TickerDisplayPart> singleCopy, bool loopForScroll)
    {
        track.Children.Clear();

        AppendParts(track, singleCopy);
        if (!loopForScroll)
        {
            return;
        }

        AppendParts(track, InternationalTeamDisplay.SeparatorParts());
        AppendParts(track, singleCopy);
        AppendParts(track, InternationalTeamDisplay.SeparatorParts());
    }

    public static bool ShouldLoopForScroll(double singleCopyWidth, double viewportWidth) =>
        viewportWidth > 0 && singleCopyWidth > viewportWidth;

    public static void MeasureTrack(StackPanel track, double viewportHeight, out double fullWidth)
    {
        track.Measure(new WinSize(1_000_000, Math.Max(viewportHeight, LineHeight)));
        fullWidth = track.DesiredSize.Width;
        if (fullWidth <= 0)
        {
            fullWidth = track.ActualWidth;
        }
    }

    public static void LayoutTrack(StackPanel track, double viewportWidth, double viewportHeight, bool centerWhenFits = false)
    {
        track.Measure(new WinSize(1_000_000, Math.Max(viewportHeight, LineHeight)));
        var trackWidth = track.DesiredSize.Width;
        if (trackWidth <= 0)
        {
            trackWidth = track.ActualWidth;
        }

        var trackHeight = track.DesiredSize.Height;
        if (trackHeight <= 0)
        {
            trackHeight = track.ActualHeight > 0 ? track.ActualHeight : LineHeight;
        }

        track.Arrange(new WinRect(0, 0, trackWidth, trackHeight));

        var left = centerWhenFits && viewportWidth > 0 && trackWidth < viewportWidth
            ? (viewportWidth - trackWidth) / 2
            : 0;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(track, left);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(
            track,
            Math.Max(0, (viewportHeight - trackHeight) / 2));
    }

    private static void AppendParts(StackPanel track, IEnumerable<TickerDisplayPart> parts)
    {
        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part.FlagImageUrl))
            {
                var leftMargin = GetFlagLeftMargin(track);
                if (TryCreateFlagElement(part.FlagImageUrl, leftMargin) is { } flagElement)
                {
                    track.Children.Add(flagElement);
                }
            }

            if (!string.IsNullOrEmpty(part.Text))
            {
                track.Children.Add(
                    part.Text == InternationalTeamDisplay.TickerSeparator
                        ? CreateSeparatorElement()
                        : CreateTextElement(part.Text, track));
            }
        }
    }

    private static double GetFlagLeftMargin(StackPanel track)
    {
        if (track.Children.Count == 0)
        {
            return 0;
        }

        if (track.Children[track.Children.Count - 1] is not TextBlock precedingText)
        {
            return 0;
        }

        var text = precedingText.Text;
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var trimmed = text.Trim();

        // Kickoff time / TBD already get right margin via TryGetTextMargins.
        if (LooksLikeKickoffTime(trimmed))
        {
            return 0;
        }

        // League bracket (e.g. "[FIFA World Cup]") — gap before flag when no kickoff time part.
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            return LeagueToTeamGap;
        }

        // Score / spaced text already includes separation (e.g. " 1-0 ").
        if (text.EndsWith(' '))
        {
            return 0;
        }

        return 4;
    }

    private static WinGrid? TryCreateFlagElement(string flagImageUrl, double leftMargin)
    {
        if (string.IsNullOrWhiteSpace(flagImageUrl)
            || !Uri.TryCreate(flagImageUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = (int)FlagWidth,
                DecodePixelHeight = (int)FlagHeight,
                UriSource = uri
            };

            var image = new WinImage
            {
                Source = bitmap,
                Width = FlagWidth,
                Height = FlagHeight,
                Stretch = WinStretch.Uniform,
                VerticalAlignment = WinVerticalAlignment.Center,
                HorizontalAlignment = WinHorizontalAlignment.Center
            };

            var host = new WinGrid
            {
                Height = LineHeight,
                VerticalAlignment = WinVerticalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(leftMargin, 0, FlagRightMargin, 0)
            };
            host.Children.Add(image);
            return host;
        }
        catch
        {
            return null;
        }
    }

    private static TextBlock CreateTextElement(string text, StackPanel? track = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = FontSize,
            LineHeight = LineHeight,
            Foreground = new WinSolidColorBrush(Microsoft.UI.Colors.White),
            FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = WinVerticalAlignment.Center
        };

        if (TryGetTextMargins(text, track, out var margin))
        {
            block.Margin = margin;
        }

        return block;
    }

    private static bool PrecededBySeparator(StackPanel track)
    {
        if (track.Children.Count == 0)
        {
            return false;
        }

        return track.Children[track.Children.Count - 1] is TextBlock { Text: "\u26bd" };
    }

    private static bool TryGetTextMargins(string text, StackPanel? track, out Microsoft.UI.Xaml.Thickness margin)
    {
        margin = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            var left = track != null && PrecededBySeparator(track) ? SeparatorSidePadding : 0;
            margin = new Microsoft.UI.Xaml.Thickness(left, 0, LeagueToTeamGap, 0);
            return true;
        }

        if (LooksLikeKickoffTime(trimmed))
        {
            margin = new Microsoft.UI.Xaml.Thickness(0, 0, KickoffTimeRightMargin, 0);
            return true;
        }

        if (trimmed.EndsWith(':'))
        {
            margin = new Microsoft.UI.Xaml.Thickness(0, 0, HeaderRightMargin, 0);
            return true;
        }

        if (trimmed.StartsWith('('))
        {
            margin = new Microsoft.UI.Xaml.Thickness(StatusLeftMargin, 0, 0, 0);
            return true;
        }

        if (trimmed.Equals("vs", StringComparison.OrdinalIgnoreCase))
        {
            margin = new Microsoft.UI.Xaml.Thickness(VsHorizontalMargin, 0, VsHorizontalMargin, 0);
            return true;
        }

        if (LooksLikeScore(trimmed))
        {
            margin = new Microsoft.UI.Xaml.Thickness(ScoreHorizontalMargin, 0, ScoreHorizontalMargin, 0);
            return true;
        }

        if (text.StartsWith(' ') && trimmed.Length > 0)
        {
            margin = new Microsoft.UI.Xaml.Thickness(0, 0, TeamNameRightMargin, 0);
            return true;
        }

        return false;
    }

    private static bool LooksLikeKickoffTime(string trimmed)
    {
        if (trimmed.Equals("TBD", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var colon = trimmed.IndexOf(':');
        if (colon < 1 || colon > 2 || colon != trimmed.Length - 3)
        {
            return false;
        }

        var hours = trimmed[..colon];
        var mins = trimmed[(colon + 1)..];
        return hours.All(static c => char.IsDigit(c))
               && mins.Length == 2
               && mins.All(static c => char.IsDigit(c));
    }

    private static bool LooksLikeScore(string trimmed)
    {
        var scorePart = trimmed;
        const string aggregatePrefix = "agg ";
        if (scorePart.StartsWith(aggregatePrefix, StringComparison.OrdinalIgnoreCase))
        {
            scorePart = scorePart[aggregatePrefix.Length..].Trim();
        }

        var aggIndex = scorePart.IndexOf(" agg ", StringComparison.OrdinalIgnoreCase);
        if (aggIndex >= 0)
        {
            scorePart = scorePart[..aggIndex].Trim();
        }

        var dash = scorePart.IndexOf('-');
        if (dash <= 0 || dash >= scorePart.Length - 1)
        {
            return false;
        }

        var home = scorePart[..dash].Trim();
        var away = scorePart[(dash + 1)..].Trim();
        return home.Length > 0
               && away.Length > 0
               && home.All(static c => char.IsDigit(c) || c == '-')
               && away.All(static c => char.IsDigit(c) || c == '-');
    }

    private static TextBlock CreateSeparatorElement() =>
        new()
        {
            Text = "\u26bd",
            FontSize = FontSize,
            LineHeight = LineHeight,
            Foreground = new WinSolidColorBrush(Microsoft.UI.Colors.White),
            FontFamily = new FontFamily("Segoe UI"),
            VerticalAlignment = WinVerticalAlignment.Center,
            Margin = new Microsoft.UI.Xaml.Thickness(SeparatorSidePadding, 0, SeparatorSidePadding, 0)
        };
}
