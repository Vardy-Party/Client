using System.ComponentModel;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Shapes;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// One match card. Animations are deliberately cheap (opacity + transform
/// only): the live-dot pulse, the focus/hover scale, the focus-ring fade, the
/// sheen sweep, and the selected/resolving veil pulse. All are stopped when
/// the card unloads so virtualised lists stay light.
/// Focus chrome is tuned for the 10-foot TV experience: a strong scale bump
/// plus a bright pre-built focus ring faded in over ~130ms, and a distinct
/// gold "resolving" state so a clicked card visibly stays active while
/// streams resolve. Nothing on the focus path mutates the MAUI Shadow or the
/// card's stroke: both trigger blur/layout re-renders that jank low-end TV
/// hardware. Rapid D-pad autorepeat coalesces: bursts apply chrome instantly
/// (no animation pile-up) and the deliberate single move gets the full glide.
/// </summary>
public partial class MatchCardView : ContentView
{
    private const string PulseAnimation = "LiveDotPulse";
    private const string SheenAnimation = "SheenSweep";
    private const string ResolvingPulseAnimation = "ResolvingPulse";
    private const uint HoverScaleMs = 130;

    // 1.045 was invisible from the sofa; 1.09 plus the bright ring reads at 10 feet.
    private const double FocusScale = 1.09;
    private const double ResolvingScale = 1.06;

    // Focus moves closer together than this (D-pad autorepeat) skip animation:
    // chrome snaps instantly so held-key runs stay fluid with no pile-up.
    private const long FocusBurstMs = 200;

    private static readonly SolidColorBrush FocusRingBrush = new(Color.FromArgb("#AFCBFF"));
    private static readonly SolidColorBrush ResolvingRingBrush = new(Color.FromArgb("#FFD54F"));

    // Shared across cards deliberately: a burst is a property of the D-pad
    // stream, not of one card. UI-thread only.
    private static long _lastFocusEnterTicks;

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
#if WINDOWS
        CardOuter.HandlerChanged += OnWindowsCardHandlerChanged;
#else
        CardOuter.HandlerChanged += OnCardHandlerChanged;
        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += OnPointerEntered;
        hover.PointerExited += OnPointerExited;
        CardOuter.GestureRecognizers.Add(hover);
#endif
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
            ApplyInteractionState(animate: false);

            // Recycled containers can be rebound while still attached (no
            // Loaded), so the new VM's armed initial focus must be honoured here.
            EnableTvFocus();
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
            ApplyInteractionState(animate: true);
        }
    }

    private void ApplyCornerRadius()
    {
        var radius = ViewModel?.Layout.CardCornerRadius ?? 14;
        CardOuter.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) };
        FocusRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) };
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (ViewModel is { IsLive: true })
        {
            StartPulse();
        }

        ApplyInteractionState(animate: false);
        EnableTvFocus();
    }

#if WINDOWS
    private Microsoft.UI.Xaml.FrameworkElement? _windowsPointerElement;

    private void OnWindowsCardHandlerChanged(object? sender, EventArgs e)
    {
        DetachWindowsPointerHover();
        if (CardOuter.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            _windowsPointerElement = fe;
            fe.PointerEntered += OnWindowsPointerEntered;
            fe.PointerExited += OnWindowsPointerExited;
        }
    }

    private void DetachWindowsPointerHover()
    {
        if (_windowsPointerElement == null) return;
        _windowsPointerElement.PointerEntered -= OnWindowsPointerEntered;
        _windowsPointerElement.PointerExited -= OnWindowsPointerExited;
        _windowsPointerElement = null;
    }

    private void OnWindowsPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel?.FocusMoved();
        EnterHighlight();
    }

    private void OnWindowsPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => ExitHighlight();
#endif

    /// <summary>
    /// Runs whenever the platform view is (re)created: the native focus wiring
    /// below must follow the platform view, not the Loaded event, because MAUI
    /// can swap handlers on virtualised items without an Unloaded/Loaded pair.
    /// </summary>
    private void OnCardHandlerChanged(object? sender, EventArgs e) => EnableTvFocus();

    /// <summary>
    /// Android TV: MAUI Borders are not focusable natively, so D-pad focus
    /// would skip the cards. Make the platform view focusable and clickable —
    /// a clickable focused Android view fires Click on DPAD_CENTER/Enter.
    /// Wired only on TV idiom so phone taps don't double-fire alongside the
    /// TapGestureRecognizer. Also delivers the one-shot initial autofocus the
    /// view model arms on the first card when the grid first appears.
    /// Idempotent and called from Loaded, HandlerChanged and BindingContext
    /// changes so no timing/recycling path can leave a card unfocusable.
    /// </summary>
    private void EnableTvFocus()
    {
#if ANDROID
        if (!HomeView.IsTelevision())
        {
            return;
        }

        if (CardOuter.Handler?.PlatformView is not global::Android.Views.View native)
        {
            return;
        }

        native.Focusable = true;
        native.FocusableInTouchMode = false;
        if (native is global::Android.Views.ViewGroup group)
        {
            // D-pad focus search must land on the card root, never on one of
            // its children.
            group.DescendantFocusability = global::Android.Views.DescendantFocusability.BlockDescendants;
        }

        if (!ReferenceEquals(_wiredNative, native))
        {
            UnwireNativeTvFocus();
            _wiredNative = native;
            native.Click += OnNativeCardClick;
            native.FocusChange += OnNativeFocusChange;
            native.KeyPress += OnNativeKeyPress;
        }

        if (ViewModel?.TryConsumeInitialFocus() == true)
        {
            // Post: the view must be attached and laid out before focusing.
            native.Post(() => native.RequestFocus());
        }
#endif
    }

#if ANDROID
    private global::Android.Views.View? _wiredNative;

    private void UnwireNativeTvFocus()
    {
        if (_wiredNative is null)
        {
            return;
        }

        _wiredNative.Click -= OnNativeCardClick;
        _wiredNative.FocusChange -= OnNativeFocusChange;
        _wiredNative.KeyPress -= OnNativeKeyPress;
        _wiredNative = null;
    }

    private void OnNativeCardClick(object? sender, EventArgs e) => ViewModel?.Pick();

    /// <summary>
    /// The focused view sees D-pad keys before Android's default focus search:
    /// <see cref="TvDpadFocusRouter"/> gives down/up Netflix-style column
    /// memory and clamps left/right at row edges so focus never leaps rows.
    /// Unhandled keys fall through to the default traversal.
    /// </summary>
    private void OnNativeKeyPress(object? sender, global::Android.Views.View.KeyEventArgs e)
    {
        e.Handled = e.Event?.Action == global::Android.Views.KeyEventActions.Down
            && sender is global::Android.Views.View view
            && TvDpadFocusRouter.TryHandle(view, e.KeyCode);
    }

    private void OnNativeFocusChange(object? sender, global::Android.Views.View.FocusChangeEventArgs e)
    {
        if (e.HasFocus)
        {
            ViewModel?.FocusMoved();
            EnterHighlight();
            EnsureFocusedCardVisible();
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
        EnsureFocusedCardVisible();
    }

    private void OnCardUnfocused(object? sender, FocusEventArgs e) => ExitHighlight();

    /// <summary>
    /// Focus (D-pad/keyboard, never pointer hover) landed on this card: keep
    /// the card fully in the horizontal strip ScrollView and the row fully in
    /// the outer CollectionView. Native scrollers only reveal enough to take
    /// focus, not the focus glow. MakeVisible is a no-op when already in view.
    /// </summary>
    private void EnsureFocusedCardVisible()
    {
        if (ViewModel is null)
        {
            return;
        }

        ScrollView? strip = null;
        CollectionView? rows = null;
        LeagueRowViewModel? row = null;

        for (Element? element = this; element != null; element = element.Parent)
        {
            if (row is null && element.BindingContext is LeagueRowViewModel leagueRow)
            {
                row = leagueRow;
            }

            if (strip is null
                && element is ScrollView scroll
                && scroll.Orientation == ScrollOrientation.Horizontal)
            {
                strip = scroll;
            }

            if (element is CollectionView list)
            {
                rows = list;
                break;
            }
        }

        if (strip != null)
        {
            ObserveVisual(strip.ScrollToAsync(this, ScrollToPosition.MakeVisible, true));
        }

        if (rows != null && row != null)
        {
            rows.ScrollTo(row, position: ScrollToPosition.MakeVisible, animate: true);
        }
    }

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

#if WINDOWS
        DetachWindowsPointerHover();
#endif

#if ANDROID
        UnwireNativeTvFocus();
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
        var now = Environment.TickCount64;
        var burst = now - _lastFocusEnterTicks < FocusBurstMs;
        _lastFocusEnterTicks = now;

        _isFocused = true;
        ApplyInteractionState(animate: !burst);

        // The sheen allocates an Animation per run: a nice flourish on a
        // deliberate move, dead weight at autorepeat rate.
        if (!burst)
        {
            RunSheenSweep();
        }
    }

    private void ExitHighlight()
    {
        // During a held-key run the leaving card snaps back instantly; the
        // timestamp is not updated here (exits ride the enters' burst state).
        var burst = Environment.TickCount64 - _lastFocusEnterTicks < FocusBurstMs;

        _isFocused = false;
        ApplyInteractionState(animate: !burst);
    }

    /// <summary>
    /// One place decides the card chrome, resolving > focused > rest: gold
    /// ring + pulsing veil while the picked card resolves streams, bright
    /// ring + strong scale under focus, quiet chrome otherwise. Transitions
    /// are transform/opacity only (Scale + ring alpha) — no stroke, shadow or
    /// layout property changes — so a focus move never re-measures the card
    /// or re-renders a shadow blur. Starting a ScaleTo/FadeTo replaces any
    /// in-flight one for the same property, so rapid moves cannot pile up.
    /// </summary>
    private void ApplyInteractionState(bool animate)
    {
        var resolving = ViewModel?.IsResolving == true;

        var ringBrush = resolving ? ResolvingRingBrush : FocusRingBrush;
        if (!ReferenceEquals(FocusRing.Stroke, ringBrush))
        {
            FocusRing.Stroke = ringBrush;
        }

        if (resolving)
        {
            StartResolvingPulse();
        }
        else
        {
            StopResolvingPulse();
        }

        var targetScale = resolving ? ResolvingScale : _isFocused ? FocusScale : 1.0;
        var targetRing = resolving || _isFocused ? 1.0 : 0.0;

        if (animate)
        {
            if (!IsLoaded) return;
            ObserveVisual(CardOuter.ScaleToAsync(targetScale, HoverScaleMs, Easing.CubicOut));
            ObserveVisual(FocusRing.FadeToAsync(targetRing, HoverScaleMs, Easing.CubicOut));
        }
        else
        {
            CardOuter.CancelAnimations();
            FocusRing.CancelAnimations();
            CardOuter.Scale = targetScale;
            FocusRing.Opacity = targetRing;
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

    private static void ObserveVisual(Task animation) => _ = ObserveVisualAsync(animation);

    private static async Task ObserveVisualAsync(Task animation)
    {
        try
        {
            await animation.ConfigureAwait(true);
        }
        catch
        {
        }
    }
}
