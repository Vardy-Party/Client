using System.ComponentModel;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// The metallic Vardy Party crest in the homepage header. Decoration only:
/// it is never focusable (D-pad/tab traversal skips it; the menu button
/// beside it stays focusable).
/// While the catalog loads (<see cref="HomeViewModel.IsContentLoading"/>)
/// the crest spins on a 3D RotationY turntable; when rows render it finishes
/// the turn it is on and eases to rest with a sheen sweep. Catalog paint must
/// not abort the spinner — that froze the crest mid-turn.
/// </summary>
public partial class BrandLogoView : ContentView
{
    private const string SheenAnimation = "BrandSheenSweep";
    private const string AmbientAnimation = "BrandAmbientShimmer";
    private const string SpinAnimation = "BrandLoadingSpin";
    private const string SettleAnimation = "BrandSpinSettle";
    private const uint FocusScaleMs = 130;
    private const double AmbientOpacity = 0.35;
    private const double RimMinScaleX = 0.06;
    private const double RimMaxScaleX = 0.34;
    private const double RimDriftPx = 7.0;

    private bool _ambientRunning;
    private bool _spinning;
    private bool _settleRequested;
    private bool _restartPending;
    private bool _atRest;
    private bool _settlingInline;
    private long _settleRequestedAtMs = -1;
    private double _angle;
    private HomeViewModel? _observedViewModel;

    public BrandLogoView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();

        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel = null;
        }

        if (BindingContext is HomeViewModel vm)
        {
            _observedViewModel = vm;
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (IsLoaded)
        {
            ApplyLoadState();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(HomeViewModel.IsContentLoading) or nameof(HomeViewModel.HasError))
        {
            ApplyLoadState();
        }
    }

    private bool ShouldSpin => _observedViewModel is { IsContentLoading: true, HasError: false };

    private void ApplyLoadState()
    {
        if (!IsLoaded) return;

        if (ShouldSpin)
        {
            _settleRequested = false;
            _settleRequestedAtMs = -1;
            _atRest = false;
            StartLoadingSpin();
            return;
        }

        RequestSettle();
    }

    /// <summary>
    /// HomeView calls this after a catalog apply so a spinner killed by
    /// BindableLayout/CollectionView materialization can ease to rest from
    /// the last applied angle instead of staying edge-on. Rows are painted:
    /// even when the loading flag has not flipped yet (ShouldSpin still true)
    /// the settle is queued rather than dropped — the spinner finishes the
    /// turn it is on and eases to rest, and a later reload restarts the
    /// turntable via ApplyLoadState. The old ShouldSpin early-return left a
    /// layout-killed spinner sitting edge-on until IsContentLoading flipped.
    /// </summary>
    public void OnCatalogApplied()
    {
        if (!IsLoaded)
        {
            return;
        }

        RequestSettle();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        CrestImage.Source ??= BrandCrestImageLoader.GetCrest();
        DisableFocusTraversal();
        ApplyLoadState();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _ambientRunning = false;
        _spinning = false;
        _settleRequested = false;
        _settleRequestedAtMs = -1;
        _restartPending = false;
        this.AbortAnimation(SheenAnimation);
        this.AbortAnimation(AmbientAnimation);
        this.AbortAnimation(SpinAnimation);
        this.AbortAnimation(SettleAnimation);
        ResetSpinVisuals();
    }

    private void DisableFocusTraversal()
    {
#if ANDROID
        if (Handler?.PlatformView is global::Android.Views.ViewGroup platformGroup)
        {
            platformGroup.Focusable = false;
            platformGroup.FocusableInTouchMode = false;
            platformGroup.DescendantFocusability = global::Android.Views.DescendantFocusability.BlockDescendants;
        }
#endif
    }

    /// <summary>TV/keyboard focus entered the header: scale + glow + sheen.</summary>
    public void OnHeaderFocusEntered()
    {
        if (!IsLoaded) return;
        ObserveVisual(LogoOuter.ScaleToAsync(1.08, FocusScaleMs, Easing.CubicOut));
        ObserveVisual(GlowRing.FadeToAsync(0.7, FocusScaleMs));
        RunSheenSweep(0.85);
    }

    public void OnHeaderFocusExited()
    {
        if (!IsLoaded) return;
        ObserveVisual(LogoOuter.ScaleToAsync(1.0, FocusScaleMs, Easing.CubicOut));
        ObserveVisual(GlowRing.FadeToAsync(0.0, FocusScaleMs));
    }

    private void StartLoadingSpin()
    {
        if (_spinning) return;
        _spinning = true;
        _settleRequested = false;
        _settleRequestedAtMs = -1;
        _atRest = false;

        _ambientRunning = false;
        this.AbortAnimation(AmbientAnimation);
        this.AbortAnimation(SheenAnimation);
        this.AbortAnimation(SettleAnimation);

        var spin = new Animation(ApplySpinFrame, 0, 360);
        spin.Commit(
            this,
            SpinAnimation,
            length: BrandCrestSpin.TurnMs,
            easing: Easing.Linear,
            finished: OnSpinCycleFinished,
            repeat: () => BrandCrestSpin.ContinueSpinCycle(_spinning, _settleRequested));
    }

    private void OnSpinCycleFinished(double _, bool cancelled)
    {
        if (_settlingInline)
        {
            // SettleFromAngle is force-aborting a zombie turn; it owns the
            // rest of this pass.
            return;
        }

        if (_settleRequested || !ShouldSpin)
        {
            // A queued settle outranks a restart: the rows are painted, so
            // the crest eases to rest even if the cycle was layout-cancelled.
            if (IsLoaded)
            {
                SettleFromAngle(_angle);
            }

            return;
        }

        if (cancelled)
        {
            // Layout aborted the spinner (catalog materializing). Do not
            // Commit synchronously — that fights the same layout pass — and
            // never Dispatcher.Dispatch visual-tree work from this
            // layout-adjacent callback on Windows: that is the stowed-
            // exception class (0x800710DD/0xc000027b) the catalog apply pump
            // exists to avoid. Defer the restart the same way the catalog
            // defers its apply.
            _spinning = false;
            if (IsLoaded)
            {
                _restartPending = true;
                ScheduleCrestTick();
            }

            return;
        }
    }

    /// <summary>
    /// True while the crest has deferred work (a spin restart after a layout
    /// abort, or a settle that has not reached face-on rest). On Windows,
    /// <see cref="HomeView"/> keeps its 50ms UI-thread apply pump ticking
    /// while this is set.
    /// </summary>
    public bool HasPendingCrestWork => _restartPending || (_settleRequested && !_atRest);

#if WINDOWS
    /// <summary>
    /// Raised when deferred crest work needs <see cref="HomeView"/>'s
    /// UI-thread apply pump started (the crest never starts its own timer).
    /// </summary>
    public event Action? PumpRequested;

    /// <summary>Apply-pump tick: run deferred crest work on the UI thread.</summary>
    public void PumpCrest() => OnCrestTick();
#endif

    private void ScheduleCrestTick()
    {
#if WINDOWS
        // Routed through HomeView's 50ms UI-thread apply pump — the same
        // mechanism the catalog apply uses instead of Dispatcher.Dispatch
        // into WinUI layout.
        PumpRequested?.Invoke();
#else
        // Android/Desktop: a posted continuation, like the catalog's
        // MainThread flush. Deliberately not an IDispatcherTimer — Android TV
        // skips timer ticks under Choreographer load.
        Dispatcher.Dispatch(OnCrestTick);
#endif
    }

    private void OnCrestTick()
    {
        if (!IsLoaded)
        {
            _restartPending = false;
            return;
        }

        if (_settleRequested && !_atRest)
        {
            // Re-drive a pending settle: wait for a live turn to finish,
            // retry an aborted ease, or snap once overdue. The settle
            // outranks any queued restart — the rows are painted.
            _restartPending = false;
            if (!this.AnimationIsRunning(SettleAnimation))
            {
                SettleFromAngle(_angle);
            }

            return;
        }

        if (_restartPending)
        {
            _restartPending = false;
            if (ShouldSpin && !_spinning)
            {
                StartLoadingSpin();
            }
        }
    }

    private void RequestSettle()
    {
        if (_atRest && BrandCrestSpin.IsFaceOnRest(_angle))
        {
            return;
        }

        if (!_settleRequested)
        {
            _settleRequested = true;
            _settleRequestedAtMs = Environment.TickCount64;
        }

        _restartPending = false;

        if (this.AnimationIsRunning(SettleAnimation))
        {
            // The ease is already in flight; its finished callback completes
            // (or retries) the rest. Restarting it on every catalog/image
            // flush would keep resetting the 480ms ease.
            return;
        }

        if (BrandCrestSpin.SettleNowBecauseSpinDied(_settleRequested, this.AnimationIsRunning(SpinAnimation)))
        {
            SettleFromAngle(_angle);
            return;
        }

        // The turn in flight stops repeating (ContinueSpinCycle) and
        // OnSpinCycleFinished settles — an animation-frame tick that fires on
        // every platform. Also arm the deferred crest tick as the watchdog:
        // on Windows it keeps the apply pump running until the crest rests;
        // on Android/Desktop it is a posted re-check. Never an
        // IDispatcherTimer — Android TV starves those under Choreographer
        // load, which used to leave the crest edge-on.
        ScheduleCrestTick();
    }

    private void SettleFromAngle(double current)
    {
        _spinning = false;
        if (!_settleRequested)
        {
            _settleRequested = true;
            _settleRequestedAtMs = Environment.TickCount64;
        }

        var overdue = _settleRequestedAtMs >= 0
            && Environment.TickCount64 - _settleRequestedAtMs >= BrandCrestSpin.SettleOverdueMs;

        if (this.AnimationIsRunning(SpinAnimation))
        {
            if (!overdue)
            {
                // Finish the cycle via repeat=false; do not abort mid-turn.
                return;
            }

            // The turn had a full cycle plus the ease plus slack to finish
            // and did not (zombie after a layout-abort storm): kill it.
            AbortInline(SpinAnimation);
        }

        AbortInline(SettleAnimation);

        if (overdue || BrandCrestSpin.IsFaceOnRest(current))
        {
            // Overdue settles snap: a direct property write cannot be
            // aborted by layout, so face-on rest is guaranteed.
            CompleteRest();
            return;
        }

        var from = BrandCrestSpin.NormalizeDegrees(current);
        var target = BrandCrestSpin.RestTargetDegrees(from);
        var settle = new Animation(ApplySpinFrame, from, target, Easing.CubicOut);
        settle.Commit(this, SettleAnimation, length: BrandCrestSpin.SettleMs, finished: OnSettleAnimationFinished);
    }

    private void OnSettleAnimationFinished(double _, bool cancelled)
    {
        if (_settlingInline)
        {
            // SettleFromAngle is replacing the ease it just aborted.
            return;
        }

        if (!cancelled)
        {
            CompleteRest();
            return;
        }

        // Layout aborted the settle mid-ease. Retry from the deferred crest
        // tick (Windows: apply pump; Android/Desktop: posted continuation)
        // until the crest rests — never from a timer.
        if (IsLoaded && _settleRequested)
        {
            ScheduleCrestTick();
        }
    }

    private void AbortInline(string animation)
    {
        _settlingInline = true;
        try
        {
            this.AbortAnimation(animation);
        }
        finally
        {
            _settlingInline = false;
        }
    }

    private void CompleteRest()
    {
        ResetSpinVisuals();
        _atRest = true;
        _settleRequested = false;
        _settleRequestedAtMs = -1;
        if (IsLoaded)
        {
            RunSheenSweep(0.85);
        }
    }

    private void ResetSpinVisuals()
    {
        _angle = 0;
        LogoOuter.Opacity = 1;
        LogoOuter.RotationY = 0;
        EdgeRim.Opacity = 0;
        LogoSheen.Opacity = 0;
        LogoSheen.TranslationX = -26;
    }

    private void ApplySpinFrame(double angleDegrees)
    {
        _angle = angleDegrees;
        var radians = angleDegrees * Math.PI / 180.0;
        var sin = Math.Sin(radians);
        var absSin = Math.Abs(sin);
        var faceVisibility = Math.Abs(Math.Cos(radians));

        LogoOuter.RotationY = angleDegrees;

        EdgeRim.ScaleX = RimMinScaleX + (RimMaxScaleX - RimMinScaleX) * absSin;
        EdgeRim.TranslationX = sin * RimDriftPx;
        EdgeRim.Opacity = 0.9 * absSin;

        var glint = Math.Abs(Math.Sin(2 * radians));
        LogoSheen.Opacity = faceVisibility * (0.12 + 0.68 * glint * glint * glint);
        LogoSheen.TranslationX = -26 + (SheenTravel + 26) * (angleDegrees % 180.0) / 180.0;
    }

    private double SheenTravel => Math.Max(Width, 40) + 26;

    private void RunSheenSweep(double peakOpacity)
    {
        if (_spinning || this.AnimationIsRunning(SettleAnimation)) return;

        _ambientRunning = false;
        this.AbortAnimation(AmbientAnimation);
        this.AbortAnimation(SheenAnimation);

        LogoSheen.TranslationX = -26;
        LogoSheen.Opacity = peakOpacity;

        var sweep = new Animation();
        sweep.Add(0.0, 1.0, new Animation(v => LogoSheen.TranslationX = v, -26, SheenTravel, Easing.CubicInOut));
        sweep.Add(0.6, 1.0, new Animation(v => LogoSheen.Opacity = v, peakOpacity, 0.0));
        sweep.Commit(this, SheenAnimation, length: 700, finished: (_, _) =>
        {
            LogoSheen.Opacity = 0;
            if (IsLoaded)
            {
                StartAmbientShimmer();
            }
        });
    }

    private void StartAmbientShimmer()
    {
        if (_ambientRunning) return;
        _ambientRunning = true;

        var loop = new Animation();
        loop.Add(0.00, 0.25, new Animation(v => LogoSheen.TranslationX = v, -26, SheenTravel, Easing.SinInOut));
        loop.Add(0.00, 0.05, new Animation(v => LogoSheen.Opacity = v, 0.0, AmbientOpacity));
        loop.Add(0.18, 0.25, new Animation(v => LogoSheen.Opacity = v, AmbientOpacity, 0.0));
        loop.Commit(this, AmbientAnimation, length: 6000, repeat: () => _ambientRunning);
    }

    private static void ObserveVisual(Task animation)
    {
        _ = ObserveVisualAsync(animation);
    }

    private static async Task ObserveVisualAsync(Task animation)
    {
        try
        {
            await animation.ConfigureAwait(true);
        }
        catch
        {
            // Element unloaded or handler torn down; do not throw on the XAML thread.
        }
    }
}
