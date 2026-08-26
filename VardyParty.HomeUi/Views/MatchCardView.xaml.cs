using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// One match card. Animations are deliberately cheap (opacity + transform
/// only): the live-dot pulse, the focus/hover scale, the sheen sweep, and the
/// selected/resolving veil pulse. All are stopped when the card unloads so
/// virtualised lists stay light.
/// Focus chrome is tuned for the 10-foot TV experience: a strong scale bump
/// plus a bright border and glow, and a distinct gold "resolving" state so a
/// clicked card visibly stays active while streams resolve.
/// </summary>
public partial class MatchCardView : ContentView
{
    private const string PulseAnimation = "LiveDotPulse";
    private const string SheenAnimation = "SheenSweep";
    private const string ResolvingPulseAnimation = "ResolvingPulse";
    private const uint HoverScaleMs = 130;

    // 1.045 was invisible from the sofa; 1.09 plus the glow border reads at 10 feet.
    private const double FocusScale = 1.09;
    private const double ResolvingScale = 1.06;

    private static readonly Color RestStrokeColor = Color.FromArgb("#26FFFFFF");
    private static readonly Color FocusStrokeColor = Color.FromArgb("#AFCBFF");
    private static readonly Color ResolvingStrokeColor = Color.FromArgb("#FFD54F");

    private HomeLayoutState? _observedLayout;
    private MatchCardViewModel? _observedViewModel;
    private bool _pulseRunning;
    private bool _resolvingPulseRunning;
    private bool _isFocused;

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

        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel = null;
        }

        // Recycled containers must not keep the previous card's focus chrome.
        _isFocused = false;

        if (ViewModel is { } vm)
        {
            _observedLayout = vm.Layout;
            _observedLayout.PropertyChanged += OnLayoutChanged;
            _observedViewModel = vm;
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyCornerRadius();
            ApplyInteractionState(animateScale: false);
        }
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(HomeLayoutState.CardCornerRadius))
        {
            ApplyCornerRadius();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MatchCardViewModel.IsResolving))
        {
            ApplyInteractionState(animateScale: true);
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

        ApplyInteractionState(animateScale: false);
        EnableTvFocus();
    }

    /// <summary>
    /// Android TV: MAUI Borders are not focusable natively, so D-pad focus
    /// would skip the cards. Make the platform view focusable and clickable —
    /// a clickable focused Android view fires Click on DPAD_CENTER/Enter.
    /// Wired only on TV idiom so phone taps don't double-fire alongside the
    /// TapGestureRecognizer. Also delivers the one-shot initial autofocus the
    /// view model arms on the first card when the grid first appears.
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

            if (ViewModel?.TryConsumeInitialFocus() == true)
            {
                // Post: the view must be attached and laid out before focusing.
                native.Post(() => native.RequestFocus());
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
        StopResolvingPulse();
        this.AbortAnimation(PulseAnimation);
        this.AbortAnimation(SheenAnimation);

        if (_observedLayout != null)
        {
            _observedLayout.PropertyChanged -= OnLayoutChanged;
            _observedLayout = null;
        }

        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel = null;
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
        _isFocused = true;
        ApplyInteractionState(animateScale: true);
        RunSheenSweep();
    }

    private void ExitHighlight()
    {
        _isFocused = false;
        ApplyInteractionState(animateScale: true);
    }

    /// <summary>
    /// One place decides the card chrome, resolving > focused > rest:
    /// gold pulsing veil while the picked card resolves streams, bright
    /// glow border + strong scale under focus, quiet chrome otherwise.
    /// </summary>
    private void ApplyInteractionState(bool animateScale)
    {
        var resolving = ViewModel?.IsResolving == true;

        if (resolving)
        {
            SetChrome(ResolvingStrokeColor, strokeThickness: 3, glow: true);
            StartResolvingPulse();
        }
        else
        {
            StopResolvingPulse();
            if (_isFocused)
            {
                SetChrome(FocusStrokeColor, strokeThickness: 3, glow: true);
            }
            else
            {
                SetChrome(RestStrokeColor, strokeThickness: 1, glow: false);
            }
        }

        var targetScale = resolving ? ResolvingScale : _isFocused ? FocusScale : 1.0;
        if (animateScale)
        {
            _ = CardOuter.ScaleToAsync(targetScale, HoverScaleMs, Easing.CubicOut);
        }
        else
        {
            CardOuter.Scale = targetScale;
        }
    }

    private void SetChrome(Color strokeColor, double strokeThickness, bool glow)
    {
        CardOuter.Stroke = new SolidColorBrush(strokeColor);
        CardOuter.StrokeThickness = strokeThickness;

        if (CardOuter.Shadow is { } shadow)
        {
            if (glow)
            {
                shadow.Brush = new SolidColorBrush(strokeColor);
                shadow.Opacity = 0.9f;
                shadow.Radius = 26f;
                shadow.Offset = new Point(0, 0);
            }
            else
            {
                shadow.Brush = Brush.Black;
                shadow.Opacity = 0.35f;
                shadow.Radius = 18f;
                shadow.Offset = new Point(0, 6);
            }
        }
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

    private void StartResolvingPulse()
    {
        if (_resolvingPulseRunning) return;
        _resolvingPulseRunning = true;

        var pulse = new Animation();
        pulse.Add(0.0, 0.5, new Animation(v => SelectedVeil.Opacity = v, 0.04, 0.16, Easing.SinInOut));
        pulse.Add(0.5, 1.0, new Animation(v => SelectedVeil.Opacity = v, 0.16, 0.04, Easing.SinInOut));
        pulse.Commit(this, ResolvingPulseAnimation, length: 900, repeat: () => _resolvingPulseRunning);
    }

    private void StopResolvingPulse()
    {
        if (!_resolvingPulseRunning)
        {
            SelectedVeil.Opacity = 0;
            return;
        }

        _resolvingPulseRunning = false;
        this.AbortAnimation(ResolvingPulseAnimation);
        SelectedVeil.Opacity = 0;
    }
}
