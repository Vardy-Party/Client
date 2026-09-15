using Microsoft.Maui.Graphics;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// Service / LAN warning. Drawn, not a Label: WinUI zeros Label text inside a
/// Border after a later layout pass and leaves empty chrome.
/// </summary>
public sealed class ErrorBannerView : GraphicsView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text),
        typeof(string),
        typeof(ErrorBannerView),
        string.Empty,
        propertyChanged: OnTextChanged);

    private const double BannerHeight = 44;
    private readonly BannerDrawable _drawable = new();

    public ErrorBannerView()
    {
        Drawable = _drawable;
        HeightRequest = 0;
        HorizontalOptions = LayoutOptions.Fill;
        InputTransparent = true;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (ErrorBannerView)bindable;
        var text = newValue as string ?? string.Empty;
        view._drawable.Text = text;
        var show = !string.IsNullOrWhiteSpace(text);
        view.HeightRequest = show ? BannerHeight : 0;
        view.IsVisible = show;
        view.Invalidate();
    }

    private sealed class BannerDrawable : IDrawable
    {
        private static readonly Color Fill = Color.FromArgb("#33BE1233");
        private static readonly Color Stroke = Color.FromArgb("#59FF6B7C");
        private static readonly Color Copy = Color.FromArgb("#FECACA");

        public string Text { get; set; } = string.Empty;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (string.IsNullOrWhiteSpace(Text) || dirtyRect.Width <= 0 || dirtyRect.Height <= 0)
            {
                return;
            }

            canvas.FillColor = Fill;
            canvas.FillRoundedRectangle(dirtyRect, 10);
            canvas.StrokeColor = Stroke;
            canvas.StrokeSize = 1;
            canvas.DrawRoundedRectangle(dirtyRect.Inflate(-0.5f, -0.5f), 10);

            canvas.FontColor = Copy;
            canvas.FontSize = 13;
            canvas.DrawString(
                Text,
                new RectF(dirtyRect.X + 14, dirtyRect.Y, Math.Max(0, dirtyRect.Width - 28), dirtyRect.Height),
                HorizontalAlignment.Left,
                VerticalAlignment.Center);
        }
    }
}
