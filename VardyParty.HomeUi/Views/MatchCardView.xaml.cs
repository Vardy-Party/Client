using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// One match card. Animations are deliberately cheap (opacity + transform
/// only): the live-dot pulse, the focus/hover scale, and the sheen sweep.
/// All are stopped when the card unloads so virtualised lists stay light.
/// </summary>
public partial class MatchCardView : ContentView
{
    private const string PulseAnimation = "LiveDotPulse";
    private const string SheenAnimation = "SheenSweep";
    private const uint HoverScaleMs = 130;

    private HomeLayoutState? _observedLayout;
    private bool _pulseRunning;

    public MatchCardView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        CardOuter.Focused += OnCardFocused;
        CardOuter.Unfocused += OnCardUnfocused;
    }

    private MatchCardViewModel? ViewModel => BindingContext as MatchCardViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (_observedLayout != null)
        {
            _observedLayout.PropertyChanged -= OnLayoutChanged;
            _observedLayout = null;
        }

        if (ViewModel is { } vm)
        {
            _observedLayout = vm.Layout;
            _observedLayout.PropertyChanged += OnLayoutChanged;
            ApplyCornerRadius();
        }
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(HomeLayoutState.CardCornerRadius))
        {
            ApplyCornerRadius();
        }
    }

    private void ApplyCornerRadius()
    {
        var radius = ViewModel?.Layout.CardCornerRadius ?? 14;
        CardOuter.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) };
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (ViewModel is { IsLive: true })
        {
            StartPulse();
        }

        EnableTvFocus();
    }

    /// <summary>
    /// Android TV: MAUI Borders are not focusable natively, so D-pad focus
    /// would skip the cards. Make the platform view focusable and clickable —
    /// a clickable focused Android view fires Click on DPAD_CENTER/Enter.
    /// Wired only on TV idiom so phone taps don't double-fire alongside the
    /// TapGestureRecognizer.
    /// </summary>
    private void EnableTvFocus()
    {
#if ANDROID
        if (!HomeView.IsTelevision())
        {
            return;
        }

        if (CardOuter.Handler?.PlatformView is global::Android.Views.View native)
        {
            native.Focusable = true;
            native.FocusableInTouchMode = false;
            if (!_tvClickWired)
            {
                _tvClickWired = true;
                native.Click += OnNativeCardClick;
                native.FocusChange += OnNativeFocusChange;
            }
        }
#endif
    }

#if ANDROID
    private bool _tvClickWired;

    private void OnNativeCardClick(object? sender, EventArgs e) => ViewModel?.Pick();

    private void OnNativeFocusChange(object? sender, global::Android.Views.View.FocusChangeEventArgs e)
    {
        if (e.HasFocus)
        {
            ViewModel?.FocusMoved();
            EnterHighlight();
        }
        else
        {
            ExitHighlight();
        }
    }
#endif

    private void OnCardFocused(object? sender, FocusEventArgs e)
    {
        ViewModel?.FocusMoved();
        EnterHighlight();
    }

    private void OnCardUnfocused(object? sender, FocusEventArgs e) => ExitHighlight();

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _pulseRunning = false;
        this.AbortAnimation(PulseAnimation);
        this.AbortAnimation(SheenAnimation);

        if (_observedLayout != null)
        {
            _observedLayout.PropertyChanged -= OnLayoutChanged;
            _observedLayout = null;
        }

#if ANDROID
        if (_tvClickWired && CardOuter.Handler?.PlatformView is global::Android.Views.View native)
        {
            native.Click -= OnNativeCardClick;
            native.FocusChange -= OnNativeFocusChange;
            _tvClickWired = false;
        }
#endif
    }

    private void StartPulse()
    {
        if (_pulseRunning) return;
        _pulseRunning = true;

        var pulse = new Animation();
        pulse.Add(0.0, 0.5, new Animation(v => LiveDot.Opacity = v, 1.0, 0.25, Easing.SinInOut));
        pulse.Add(0.5, 1.0, new Animation(v => LiveDot.Opacity = v, 0.25, 1.0, Easing.SinInOut));
        pulse.Commit(this, PulseAnimation, length: 1400, repeat: () => _pulseRunning);
    }

    private void OnCardTapped(object? sender, TappedEventArgs e) => ViewModel?.Pick();

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        ViewModel?.FocusMoved();
        EnterHighlight();
    }

    private void OnPointerExited(object? sender, PointerEventArgs e) => ExitHighlight();

    private void EnterHighlight()
    {
        CardOuter.Stroke = new SolidColorBrush(Color.FromArgb("#66FFFFFF"));
        _ = CardOuter.ScaleToAsync(1.045, HoverScaleMs, Easing.CubicOut);
        RunSheenSweep();
    }

    private void ExitHighlight()
    {
        CardOuter.Stroke = new SolidColorBrush(Color.FromArgb("#26FFFFFF"));
        _ = CardOuter.ScaleToAsync(1.0, HoverScaleMs, Easing.CubicOut);
    }

    private void RunSheenSweep()
    {
        this.AbortAnimation(SheenAnimation);

        var travel = (ViewModel?.Layout.CardWidth ?? 350) + 160;
        Sheen.TranslationX = -160;
        Sheen.Opacity = 0.9;

        var sweep = new Animation();
        sweep.Add(0.0, 1.0, new Animation(v => Sheen.TranslationX = v, -160, travel, Easing.CubicInOut));
        sweep.Add(0.6, 1.0, new Animation(v => Sheen.Opacity = v, 0.9, 0.0));
        sweep.Commit(this, SheenAnimation, length: 750);
    }
}
