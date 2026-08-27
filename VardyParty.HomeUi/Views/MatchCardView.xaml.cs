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
    private const string EventFlashAnimation = "MatchEventFlash";
    private const uint HoverScaleMs = 130;

    // 1.045 was invisible from the sofa; 1.09 plus the bright ring reads at 10 feet.
    private const double FocusScale = 1.09;
    private const double ResolvingScale = 1.06;

    // Focus moves closer together than this (D-pad autorepeat) skip animation:
    // chrome snaps instantly so held-key runs stay fluid with no pile-up.
    private const long FocusBurstMs = 200;

    private static readonly SolidColorBrush FocusRingBrush = new(Color.FromArgb("#AFCBFF"));

    // TV ring: brighter, closer to white — #AFCBFF at 3px was invisible at 10
    // feet on the field TV. Paired with Layout.FocusRingThickness (5px on TV).
    private static readonly SolidColorBrush TvFocusRingBrush = new(Color.FromArgb("#E2ECFF"));
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
            _observedViewModel.FlashRequested -= OnFlashRequested;
            _observedViewModel = null;
        }

        // Recycled containers must not keep the previous card's focus chrome.
        _isFocused = false;
        ResetEventFlash();

        if (ViewModel is { } vm)
        {
            _observedLayout = vm.Layout;
            _observedLayout.PropertyChanged += OnLayoutChanged;
            _observedViewModel = vm;
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _observedViewModel.FlashRequested += OnFlashRequested;
            ApplyCornerRadius();
            ApplyCardChrome();
            ApplyInteractionState(animate: false);
            if (IsLoaded)
            {
                ApplyLivePulseState();
            }

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

        if (e.PropertyName is null or nameof(HomeLayoutState.Class))
        {
            ApplyLivePulseState();
        }

        if (e.PropertyName is null or nameof(HomeLayoutState.FlatCardChrome))
        {
            ApplyCardChrome();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MatchCardViewModel.IsResolving))
        {
            ApplyInteractionState(animate: true);
        }

        // In-place catalog refreshes can flip a card live/idle without a
        // rebind; the pulse policy must follow.
        if (e.PropertyName is null or nameof(MatchCardViewModel.IsLive))
        {
            ApplyLivePulseState();
        }
    }

    private void ApplyCornerRadius()
    {
        var radius = ViewModel?.Layout.CardCornerRadius ?? 14;
        CardOuter.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) };
        FocusRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(radius) };
    }

    // XAML-declared shadows, captured once so the TV raster budget can strip
    // and (on reclassification) restore them.
    private Shadow? _cardShadow;
    private Shadow? _homeBadgeShadow;
    private Shadow? _homeMonogramShadow;
    private Shadow? _awayBadgeShadow;
    private Shadow? _awayMonogramShadow;
    private bool _shadowsCaptured;

    /// <summary>
    /// TV raster budget (<see cref="HomeLayoutState.FlatCardChrome"/>): the
    /// card drop shadow and the four badge shadows are composition blurs that
    /// re-render on every invalidation — a large slice of the 1.3s full-tree
    /// pass on the 32-bit box. On TV the card goes flat with a slightly
    /// stronger border for definition; other classes keep the full chrome.
    /// </summary>
    private void ApplyCardChrome()
    {
        if (!_shadowsCaptured)
        {
            _cardShadow = CardOuter.Shadow;
            _homeBadgeShadow = HomeBadgeChrome.Shadow;
            _homeMonogramShadow = HomeMonogramChrome.Shadow;
            _awayBadgeShadow = AwayBadgeChrome.Shadow;
            _awayMonogramShadow = AwayMonogramChrome.Shadow;
            _shadowsCaptured = true;
        }

        var flat = ViewModel?.Layout.FlatCardChrome == true;

        // Shadow is declared non-nullable but null IS its default (no shadow);
        // assigning null is the supported way to remove one.
        CardOuter.Shadow = (flat ? null : _cardShadow)!;
        HomeBadgeChrome.Shadow = (flat ? null : _homeBadgeShadow)!;
        HomeMonogramChrome.Shadow = (flat ? null : _homeMonogramShadow)!;
        AwayBadgeChrome.Shadow = (flat ? null : _awayBadgeShadow)!;
        AwayMonogramChrome.Shadow = (flat ? null : _awayMonogramShadow)!;
        CardOuter.Stroke = flat ? FlatCardStrokeBrush : DefaultCardStrokeBrush;

        // Bind-time only (never on the focus path): the 10-foot focus ring is
        // thicker on TV. Focus moves only fade the pre-built ring in and out.
        FocusRing.StrokeThickness = ViewModel?.Layout.FocusRingThickness ?? 3;
    }

    private static readonly SolidColorBrush DefaultCardStrokeBrush = new(Color.FromArgb("#26FFFFFF"));
    private static readonly SolidColorBrush FlatCardStrokeBrush = new(Color.FromArgb("#3DFFFFFF"));

    private void OnLoaded(object? sender, EventArgs e)
    {
        ApplyLivePulseState();
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

        // TV field report: "click right at the right-most card → it shifts
        // immediately, jumps back, then scrolls on". Android's focus system
        // auto-reveals a newly focused off-screen view by INSTANTLY scrolling
        // its ancestors (HorizontalScrollView.requestChildFocus →
        // scrollToChild — the same reveal requestChildRectangleOnScreen
        // serves), and MAUI's ScrollView never learns about it, so our
        // animated ScrollToAsync then computed from the stale position.
        // The platform containers gate that reveal on the FOCUSED view's
        // revealOnFocusHint (API 25+): clearing it turns the strip
        // container's auto-reveal into a no-op for card focus changes and
        // leaves exactly one scroll owner — the animated
        // EnsureFocusedCardVisible scroll below. (The vertical rows
        // RecyclerView ignores this hint; TvDpadFocusRouter owns that axis
        // by smooth-scrolling BEFORE it moves focus, which
        // RecyclerView.LayoutManager.onRequestChildFocus detects via
        // isSmoothScrolling() and skips its own requestChildOnScreen.)
        if (OperatingSystem.IsAndroidVersionAtLeast(25))
        {
            native.RevealOnFocusHint = false;
        }

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
            // Post: scale/ring and BindableLayout layout may not have settled
            // on the same focus callback; MakeVisible after the next frame
            // keeps the whole card (glow included) in the strip.
            if (sender is global::Android.Views.View native)
            {
                native.Post(EnsureFocusedCardVisible);
            }
            else
            {
                EnsureFocusedCardVisible();
            }
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
#if ANDROID
        // When the native TV focus bridge is wired, OnNativeFocusChange owns
        // scroll-into-view (it posts EnsureFocusedCardVisible after the next
        // frame, which is the reliable path once the scale/ring have applied).
        // Scrolling here too issued two ScrollToAsync calls per D-pad move.
        if (_wiredNative is null)
        {
            EnsureFocusedCardVisible();
        }
#else
        EnsureFocusedCardVisible();
#endif
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

        // Walk from the focusable chrome (CardOuter), not the ContentView
        // wrapper: BindableLayout parents the MatchCardView, and MakeVisible
        // must use the same bounds as the 1.09 focus scale + ring.
        for (Element? element = CardOuter; element != null; element = element.Parent)
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
            ObserveVisual(strip.ScrollToAsync(CardOuter, ScrollToPosition.MakeVisible, true));
        }

        if (rows != null && row != null)
        {
#if ANDROID
            // The D-pad router reveals the target row itself (it smooth-
            // scrolls the recycler BEFORE moving focus so the framework's
            // requestChildOnScreen stays out of the way); issuing the MAUI
            // item scroll on top would cancel that animation mid-move and
            // retarget it — the vertical flavour of two scroll owners.
            // Non-router focus paths (initial autofocus, focus restore)
            // still get the MakeVisible below.
            if (TvDpadFocusRouter.TryConsumeOwnedRowReveal())
            {
                return;
            }
#endif
            rows.ScrollTo(row, position: ScrollToPosition.MakeVisible, animate: true);
        }
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _pulseRunning = false;
        StopResolvingPulse();
        this.AbortAnimation(PulseAnimation);
        this.AbortAnimation(SheenAnimation);
        ResetEventFlash();

        if (_observedLayout != null)
        {
            _observedLayout.PropertyChanged -= OnLayoutChanged;
            _observedLayout = null;
        }

        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel.FlashRequested -= OnFlashRequested;
            _observedViewModel = null;
        }

#if WINDOWS
        DetachWindowsPointerHover();
#endif

#if ANDROID
        UnwireNativeTvFocus();
#endif
    }

    /// <summary>
    /// TV idle invariant (<see cref="VardyParty.Presentation.HomeIdleAnimationPolicy"/>):
    /// on the TV class the live dot is a STATIC treatment — ~17 concurrent
    /// infinite pulses kept the 32-bit TV box's Choreographer permanently
    /// saturated. Other classes keep the pulse.
    /// </summary>
    private void ApplyLivePulseState()
    {
        var wantsPulse = ViewModel is { IsLive: true }
            && Presentation.HomeIdleAnimationPolicy.AllowLiveDotPulse(
                ViewModel.Layout.Class);

        if (!wantsPulse)
        {
            StopPulse();
            return;
        }

        if (_pulseRunning) return;
        _pulseRunning = true;

        var pulse = new Animation();
        pulse.Add(0.0, 0.5, new Animation(v => LiveDot.Opacity = v, 1.0, 0.25, Easing.SinInOut));
        pulse.Add(0.5, 1.0, new Animation(v => LiveDot.Opacity = v, 0.25, 1.0, Easing.SinInOut));
        pulse.Commit(this, PulseAnimation, length: 1400, repeat: () => _pulseRunning);
    }

    private void StopPulse()
    {
        if (_pulseRunning)
        {
            _pulseRunning = false;
            this.AbortAnimation(PulseAnimation);
        }

        LiveDot.Opacity = 1.0;
    }

    /// <summary>
    /// Match-event flash, synchronized with the toast: a ~1.5s FINITE
    /// render-only celebration — the score pops (transform-only scale) and
    /// the card's own team-colour wash pulses brighter, then everything
    /// settles back. No stroke/shadow/layout mutation (TV raster budget) and
    /// no repeat (TV idle invariant).
    /// </summary>
    private void OnFlashRequested()
    {
        if (!IsLoaded)
        {
            return;
        }

        this.AbortAnimation(EventFlashAnimation);

        var flash = new Animation();
        flash.Add(0.00, 0.15, new Animation(v => EventFlashVeil.Opacity = v, 0.0, 0.55, Easing.CubicOut));
        flash.Add(0.15, 1.00, new Animation(v => EventFlashVeil.Opacity = v, 0.55, 0.0, Easing.CubicIn));
        flash.Add(0.00, 0.18, new Animation(v => ScoreLabel.Scale = v, 1.0, 1.30, Easing.CubicOut));
        flash.Add(0.18, 0.60, new Animation(v => ScoreLabel.Scale = v, 1.30, 1.0, Easing.SpringOut));
        flash.Commit(this, EventFlashAnimation, length: 1500);
    }

    private void ResetEventFlash()
    {
        this.AbortAnimation(EventFlashAnimation);
        EventFlashVeil.Opacity = 0;
        ScoreLabel.Scale = 1.0;
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
        var tv = ViewModel?.Layout.Class == Presentation.HomeLayoutClass.Tv;

        var ringBrush = resolving ? ResolvingRingBrush : tv ? TvFocusRingBrush : FocusRingBrush;
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

        // The focus lift stays off while resolving: the gold veil pulse owns
        // the card wash then, and the two must not stack.
        var targetLift = !resolving && _isFocused ? ViewModel?.Layout.FocusedCardLift ?? 0 : 0.0;

        if (animate)
        {
            if (!IsLoaded) return;
            ObserveVisual(CardOuter.ScaleToAsync(targetScale, HoverScaleMs, Easing.CubicOut));
            ObserveVisual(FocusRing.FadeToAsync(targetRing, HoverScaleMs, Easing.CubicOut));
            ObserveVisual(FocusVeil.FadeToAsync(targetLift, HoverScaleMs, Easing.CubicOut));
        }
        else
        {
            CardOuter.CancelAnimations();
            FocusRing.CancelAnimations();
            FocusVeil.CancelAnimations();
            CardOuter.Scale = targetScale;
            FocusRing.Opacity = targetRing;
            FocusVeil.Opacity = targetLift;
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
