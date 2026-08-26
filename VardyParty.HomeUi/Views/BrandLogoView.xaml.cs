using System.ComponentModel;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// The metallic Vardy Party crest in the homepage header. Decoration only:
/// it is never focusable (D-pad/tab traversal skips it; the menu button
/// beside it stays focusable).
/// While the catalog loads (<see cref="HomeViewModel.IsContentLoading"/>)
/// the crest spins; when rows render it eases to rest with a sheen sweep.
/// WinUI has no cheap PlaneProjection, so the loading spin uses 2D
/// <see cref="VisualElement.Rotation"/> there and <c>RotationY</c> elsewhere.
/// Settling is delayed one dispatcher interval so it does not abort an
/// animation on the same CoreMessaging tick as catalog materialization
/// (that pairing is what 0xc000027b'd, not the crest XAML itself).
/// </summary>
public partial class BrandLogoView : ContentView
{
    private const string SheenAnimation = "BrandSheenSweep";
    private const string AmbientAnimation = "BrandAmbientShimmer";
    private const string SpinAnimation = "BrandLoadingSpin";
    private const string SettleAnimation = "BrandSpinSettle";
    private const uint FocusScaleMs = 130;
    private const double AmbientOpacity = 0.35;
    private const uint SpinTurnMs = 1800;
    private const double RimMinScaleX = 0.06;
    private const double RimMaxScaleX = 0.34;
    private const double RimDriftPx = 7.0;

    private bool _ambientRunning;
    private bool _spinning;
    private HomeViewModel? _observedViewModel;
    private IDispatcherTimer? _settleDelay;

    public BrandLogoView()
    {
        InitializeComponent();
#if WINDOWS
        // MAUI Shadow is a WinUI DropShadow: keep it off the crest. The glow
        // ring is the focus chrome instead.
        LogoOuter.ClearValue(Border.ShadowProperty);
#endif
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
            CancelSettleDelay();
            StartLoadingSpin();
            return;
        }

        if (_spinning)
        {
            ScheduleSettle();
            return;
        }

        RunSheenSweep(0.85);
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        CrestImage.Source ??= BrandCrestImageLoader.GetCrest();
        DisableFocusTraversal();
        ApplyLoadState();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        CancelSettleDelay();
        _ambientRunning = false;
        _spinning = false;
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
        _ = LogoOuter.ScaleToAsync(1.08, FocusScaleMs, Easing.CubicOut);
        _ = GlowRing.FadeToAsync(0.7, FocusScaleMs);
        RunSheenSweep(0.85);
    }

    public void OnHeaderFocusExited()
    {
        _ = LogoOuter.ScaleToAsync(1.0, FocusScaleMs, Easing.CubicOut);
        _ = GlowRing.FadeToAsync(0.0, FocusScaleMs);
    }

    private void StartLoadingSpin()
    {
        if (_spinning) return;
        _spinning = true;

        _ambientRunning = false;
        this.AbortAnimation(AmbientAnimation);
        this.AbortAnimation(SheenAnimation);
        this.AbortAnimation(SettleAnimation);

        var spin = new Animation(ApplySpinFrame, 0, 360);
        spin.Commit(this, SpinAnimation, length: SpinTurnMs, easing: Easing.Linear, repeat: () => _spinning);
    }

    private void ScheduleSettle()
    {
        if (!_spinning) return;
        if (_settleDelay != null) return;

        _settleDelay = Dispatcher.CreateTimer();
        _settleDelay.Interval = TimeSpan.FromMilliseconds(400);
        _settleDelay.IsRepeating = false;
        _settleDelay.Tick += OnSettleDelayTick;
        _settleDelay.Start();
    }

    private void OnSettleDelayTick(object? sender, EventArgs e)
    {
        CancelSettleDelay();
        SettleFromSpin();
    }

    private void CancelSettleDelay()
    {
        if (_settleDelay == null) return;
        _settleDelay.Tick -= OnSettleDelayTick;
        _settleDelay.Stop();
        _settleDelay = null;
    }

    private void SettleFromSpin()
    {
        if (!_spinning) return;
        _spinning = false;
        this.AbortAnimation(SpinAnimation);

        var current = CurrentSpinAngle() % 360;
        if (current < 0) current += 360;
        var target = current <= 180 ? 0.0 : 360.0;

        var settle = new Animation(ApplySpinFrame, current, target, Easing.CubicOut);
        settle.Commit(this, SettleAnimation, length: 500, finished: (_, _) =>
        {
            ResetSpinVisuals();
            if (IsLoaded)
            {
                RunSheenSweep(0.85);
            }
        });
    }

    private double CurrentSpinAngle()
    {
#if WINDOWS
        return LogoOuter.Rotation;
#else
        return LogoOuter.RotationY;
#endif
    }

    private void ResetSpinVisuals()
    {
        LogoOuter.Opacity = 1;
#if WINDOWS
        LogoOuter.Rotation = 0;
#else
        LogoOuter.RotationY = 0;
#endif
        EdgeRim.Opacity = 0;
        LogoSheen.Opacity = 0;
        LogoSheen.TranslationX = -26;
    }

    private void ApplySpinFrame(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var sin = Math.Sin(radians);
        var absSin = Math.Abs(sin);
        var faceVisibility = Math.Abs(Math.Cos(radians));

#if WINDOWS
        LogoOuter.Rotation = angleDegrees;
#else
        LogoOuter.RotationY = angleDegrees;
#endif

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
        if (_spinning) return;

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
}
