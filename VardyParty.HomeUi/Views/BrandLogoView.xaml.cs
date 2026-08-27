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
    private bool _atRest;
    private double _angle;
    private HomeViewModel? _observedViewModel;
    private IDispatcherTimer? _settleWatchdog;

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
            CancelSettleWatchdog();
            _settleRequested = false;
            _atRest = false;
            StartLoadingSpin();
            return;
        }

        RequestSettle();
    }

    /// <summary>
    /// HomeView calls this after a catalog apply so a spinner killed by
    /// BindableLayout/CollectionView materialization can ease to rest from
    /// the last applied angle instead of staying edge-on.
    /// </summary>
    public void OnCatalogApplied()
    {
        if (!IsLoaded || ShouldSpin)
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
        CancelSettleWatchdog();
        _ambientRunning = false;
        _spinning = false;
        _settleRequested = false;
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
        if (ShouldSpin && cancelled)
        {
            // Layout aborted the spinner (catalog materializing). Do not
            // Commit synchronously — that fights the same layout pass.
            _spinning = false;
            if (IsLoaded)
            {
                Dispatcher.Dispatch(() =>
                {
                    if (IsLoaded && ShouldSpin && !_spinning)
                    {
                        StartLoadingSpin();
                    }
                });
            }

            return;
        }

        if (_settleRequested || !ShouldSpin)
        {
            SettleFromAngle(_angle);
        }
    }

    private void RequestSettle()
    {
        if (_atRest && BrandCrestSpin.IsFaceOnRest(_angle))
        {
            return;
        }

        _settleRequested = true;
        if (BrandCrestSpin.SettleNowBecauseSpinDied(_settleRequested, this.AnimationIsRunning(SpinAnimation)))
        {
            SettleFromAngle(_angle);
            return;
        }

        EnsureSettleWatchdog();
    }

    private void EnsureSettleWatchdog()
    {
        if (_settleWatchdog != null)
        {
            return;
        }

        _settleWatchdog = Dispatcher.CreateTimer();
        _settleWatchdog.Interval = TimeSpan.FromMilliseconds(
            BrandCrestSpin.TurnMs + BrandCrestSpin.SettleMs + 200);
        _settleWatchdog.IsRepeating = false;
        _settleWatchdog.Tick += OnSettleWatchdogTick;
        _settleWatchdog.Start();
    }

    private void OnSettleWatchdogTick(object? sender, EventArgs e)
    {
        CancelSettleWatchdog();
        if (_atRest || this.AnimationIsRunning(SettleAnimation))
        {
            return;
        }

        SettleFromAngle(_angle, snapIfIdle: true);
    }

    private void CancelSettleWatchdog()
    {
        if (_settleWatchdog == null) return;
        _settleWatchdog.Tick -= OnSettleWatchdogTick;
        _settleWatchdog.Stop();
        _settleWatchdog = null;
    }

    private void SettleFromAngle(double current, bool snapIfIdle = false)
    {
        _spinning = false;
        _settleRequested = true;

        if (this.AnimationIsRunning(SpinAnimation))
        {
            // Finish the cycle via repeat=false; do not abort mid-turn.
            if (!snapIfIdle)
            {
                return;
            }

            this.AbortAnimation(SpinAnimation);
        }

        this.AbortAnimation(SettleAnimation);

        if (BrandCrestSpin.IsFaceOnRest(current))
        {
            CompleteRest();
            return;
        }

        var from = BrandCrestSpin.NormalizeDegrees(current);
        var target = BrandCrestSpin.RestTargetDegrees(from);
        var settle = new Animation(ApplySpinFrame, from, target, Easing.CubicOut);
        settle.Commit(this, SettleAnimation, length: BrandCrestSpin.SettleMs, finished: (_, cancelled) =>
        {
            if (!cancelled)
            {
                CompleteRest();
            }
        });
    }

    private void CompleteRest()
    {
        CancelSettleWatchdog();
        ResetSpinVisuals();
        _atRest = true;
        _settleRequested = false;
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
