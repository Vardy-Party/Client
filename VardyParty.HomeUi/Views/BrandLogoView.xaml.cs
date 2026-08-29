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

    // Lifecycle decisions (spin/abort/restart/settle/snap) live in the pure,
    // clock-injectable BrandCrestSpinMachine; this view only executes Steps
    // and feeds animation facts back in.
    private readonly BrandCrestSpinMachine _crest = new();

    private bool _ambientRunning;
    private bool _settlingInline;
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
            RunStep(_crest.BeginLoading());
            return;
        }

        RunStep(RequestSettleStep());
    }

    /// <summary>
    /// HomeView calls this after a catalog apply. Ready applies (API data
    /// present — IsContentLoading false) queue the settle so a spinner killed
    /// by BindableLayout/CollectionView materialization eases to rest from
    /// its last angle instead of staying edge-on. NOT-ready applies (pre-API
    /// null/empty boards) never settle: the crest keeps spinning until real
    /// content lands, and the machine self-heals a layout-killed turn.
    /// </summary>
    public void OnCatalogApplied()
    {
        if (!IsLoaded)
        {
            return;
        }

        RunStep(_crest.CatalogApplied(
            contentReady: _observedViewModel is { IsContentLoading: false },
            this.AnimationIsRunning(SpinAnimation),
            this.AnimationIsRunning(SettleAnimation),
            BrandCrestSpin.IsFaceOnRest(_angle)));
    }

    private BrandCrestSpinMachine.Step RequestSettleStep() =>
        _crest.RequestSettle(
            this.AnimationIsRunning(SpinAnimation),
            this.AnimationIsRunning(SettleAnimation),
            BrandCrestSpin.IsFaceOnRest(_angle));

    /// <summary>Execute a machine decision; the machine never touches visuals.</summary>
    private void RunStep(BrandCrestSpinMachine.Step step)
    {
        switch (step)
        {
            case BrandCrestSpinMachine.Step.StartSpin:
                StartLoadingSpin();
                break;
            case BrandCrestSpinMachine.Step.SettleAnimated:
                ExecuteSettle(snap: false);
                break;
            case BrandCrestSpinMachine.Step.SnapToRest:
                ExecuteSettle(snap: true);
                break;
        }

        // Whatever the step was, deferred work left behind arms the tick
        // chain (Step.Defer always implies HasDeferredWork; SettleAnimated
        // leaves SettleRequested set until rest). This is the liveness
        // guarantee: a settle in flight is watched by ticks that do not
        // depend on animation callbacks — on the Desktop head's Avalonia
        // backend those callbacks can simply never fire, which froze the
        // crest mid-turn with no snap to rescue it.
        if (_crest.HasDeferredWork)
        {
            ScheduleCrestTick();
        }
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        // Crest SVG→PNG is expensive; never block first paint (TV ANR).
        _ = EnsureCrestAsync();
        DisableFocusTraversal();
        ApplyLoadState();
    }

    private async Task EnsureCrestAsync()
    {
        if (CrestImage.Source != null)
        {
            return;
        }

        try
        {
            var source = await BrandCrestImageLoader.GetCrestAsync().ConfigureAwait(false);
            if (source is null)
            {
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (IsLoaded)
                {
                    CrestImage.Source ??= source;
                }
            });
        }
        catch
        {
            // Crest is decorative; leave the spin chrome without an image.
        }
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _ambientRunning = false;
        this.AbortAnimation(SheenAnimation);
        this.AbortAnimation(AmbientAnimation);
        this.AbortAnimation(SpinAnimation);
        this.AbortAnimation(SettleAnimation);
        _crest.Reset();
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
            repeat: () => _crest.ShouldContinueSpin);
    }

    private void OnSpinCycleFinished(double _, bool cancelled)
    {
        if (_settlingInline)
        {
            // ExecuteSettle is force-aborting a zombie turn; it owns the rest
            // of this pass.
            return;
        }

        // The machine decides: a queued settle outranks a restart, a layout
        // abort while loading defers the restart (never Dispatcher.Dispatch
        // from this layout-adjacent callback on Windows — the stowed-
        // exception class 0x800710DD/0xc000027b the apply pump exists to
        // avoid), and a natural cycle rollover is a no-op.
        var step = _crest.SpinCycleFinished(cancelled, ShouldSpin);
        if (IsLoaded)
        {
            RunStep(step);
        }
    }

    /// <summary>
    /// True while the crest has deferred work (a spin restart after a layout
    /// abort, or a settle that has not reached face-on rest). On Windows,
    /// <see cref="HomeView"/> keeps its 50ms UI-thread apply pump ticking
    /// while this is set.
    /// </summary>
    public bool HasPendingCrestWork => _crest.HasDeferredWork;

#if WINDOWS
    /// <summary>
    /// Raised when deferred crest work needs <see cref="HomeView"/>'s
    /// UI-thread apply pump started (the crest never starts its own timer).
    /// </summary>
    public event Action? PumpRequested;

    /// <summary>Apply-pump tick: run deferred crest work on the UI thread.</summary>
    public void PumpCrest() => OnCrestTick();
#endif

#if !WINDOWS
    /// <summary>Matches the Windows apply pump's 50ms cadence.</summary>
    private const int CrestTickIntervalMs = 50;

    private bool _crestTickScheduled;
#endif

    private void ScheduleCrestTick()
    {
#if WINDOWS
        // Routed through HomeView's 50ms UI-thread apply pump — the same
        // mechanism the catalog apply uses instead of Dispatcher.Dispatch
        // into WinUI layout. The pump keeps ticking while HasPendingCrestWork.
        PumpRequested?.Invoke();
#else
        // Android/Desktop: a BOUNDED chain of one-shot posted continuations
        // (Task.Delay → MainThread), like the catalog's MainThread flush.
        // Deliberately not an IDispatcherTimer (Android TV skips timer ticks
        // under Choreographer load) and not Dispatcher-driven animation
        // callbacks (the Desktop head's Avalonia backend can stall them
        // entirely). The chain terminates: it only re-arms while the machine
        // has deferred work, and an unresolved settle snaps once overdue —
        // so an idle homepage schedules zero recurring work (TV invariant).
        if (_crestTickScheduled)
        {
            return;
        }

        _crestTickScheduled = true;
        _ = Task.Delay(CrestTickIntervalMs).ContinueWith(
            _ => MainThread.BeginInvokeOnMainThread(OnCrestTick),
            TaskScheduler.Default);
#endif
    }

    private void OnCrestTick()
    {
#if !WINDOWS
        _crestTickScheduled = false;
#endif
        if (!IsLoaded)
        {
            return;
        }

        // Re-drive whatever is pending: restart an aborted turn, retry an
        // aborted ease, wait on in-flight animations, or snap once overdue.
        RunStep(_crest.DeferredTick(
            ShouldSpin,
            this.AnimationIsRunning(SpinAnimation),
            this.AnimationIsRunning(SettleAnimation),
            BrandCrestSpin.IsFaceOnRest(_angle)));
    }

    /// <summary>
    /// Ease (or, for overdue settles, snap) from the current angle to face-on
    /// rest. The machine guarantees no live turn should survive this call, so
    /// any still-registered animation is a zombie and is aborted inline.
    /// </summary>
    private void ExecuteSettle(bool snap)
    {
        AbortInline(SpinAnimation);
        AbortInline(SettleAnimation);

        if (snap || BrandCrestSpin.IsFaceOnRest(_angle))
        {
            // Snapping is a direct property write that layout cannot abort:
            // the guaranteed terminal state.
            CompleteRest();
            return;
        }

        var from = BrandCrestSpin.NormalizeDegrees(_angle);
        var target = BrandCrestSpin.RestTargetDegrees(from);
        var settle = new Animation(ApplySpinFrame, from, target, Easing.CubicOut);
        settle.Commit(this, SettleAnimation, length: BrandCrestSpin.SettleMs, finished: OnSettleAnimationFinished);
    }

    private void OnSettleAnimationFinished(double _, bool cancelled)
    {
        if (_settlingInline)
        {
            // ExecuteSettle is replacing the ease it just aborted.
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
        var step = _crest.SettleAnimationFinished(cancelled: true);
        if (IsLoaded)
        {
            RunStep(step);
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
        _crest.RestCompleted();
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
        CrestFace.ScaleX = 1;
        CrestFace.Opacity = 1;
        EdgeRim.Opacity = 0;
        LogoSheen.Opacity = 0;
        LogoSheen.TranslationX = -26;
    }

    private void ApplySpinFrame(double angleDegrees)
    {
        _angle = angleDegrees;
        var radians = angleDegrees * Math.PI / 180.0;
        var sin = Math.Sin(radians);
        var cos = Math.Cos(radians);
        var absSin = Math.Abs(sin);
        var faceVisibility = Math.Abs(cos);

        // Ring chrome still uses RotationY (works on every head). The crest
        // face is a sibling driven by ScaleX so WinUI/Android actually paint
        // the raster — nested Image under RotationY was the empty circle.
        LogoOuter.RotationY = angleDegrees;
        CrestFace.ScaleX = cos;
        CrestFace.Opacity = Math.Clamp(faceVisibility, 0.08, 1.0);

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
        if (_crest.Spinning || this.AnimationIsRunning(SettleAnimation)) return;

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
        // TV idle invariant (HomeIdleAnimationPolicy): the ambient loop is the
        // second permanent tick source on the TV class (with the live-dot
        // pulses) — an idle TV homepage must schedule zero recurring work.
        // The crest still sheens on focus change (OnHeaderFocusEntered) and
        // after a settle (both one-shot).
        var layoutClass = _observedViewModel?.Layout.Class
            ?? Presentation.HomeLayoutClass.Desktop;
        if (!Presentation.HomeIdleAnimationPolicy.AllowAmbientCrestShimmer(layoutClass))
        {
            return;
        }

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
