using System.ComponentModel;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// The 3D metallic Vardy Party crest in the homepage header. Decoration only:
/// it is never focusable (D-pad/tab traversal skips it entirely; the menu
/// button beside it stays focusable).
/// While the catalog loads (<see cref="HomeViewModel.IsContentLoading"/> —
/// real state, never a timer) the crest spins on a 3D RotationY turntable
/// with a coin-edge rim whose width and shading derive from the rotation
/// angle, plus a specular glint tracking the same angle; when the first rows
/// render it eases to rest with a sheen sweep. At rest: the static metallic
/// look with an occasional ambient shimmer.
/// Follows the same performance discipline as the match cards: every frame is
/// opacity and transform only (no per-pixel effects — smooth on armeabi-v7a),
/// and everything is aborted on unload so nothing runs while off screen.
/// </summary>
public partial class BrandLogoView : ContentView
{
    private const string SheenAnimation = "BrandSheenSweep";
    private const string AmbientAnimation = "BrandAmbientShimmer";
    private const string SpinAnimation = "BrandLoadingSpin";
    private const string SettleAnimation = "BrandSpinSettle";
    private const uint FocusScaleMs = 130;
    private const double AmbientOpacity = 0.35;

    /// <summary>One full turntable revolution while loading.</summary>
    private const uint SpinTurnMs = 1800;

    // Coin-edge rim: hairline when face-on, widest when edge-on.
    private const double RimMinScaleX = 0.06;
    private const double RimMaxScaleX = 0.34;
    private const double RimDriftPx = 7.0;

    private bool _ambientRunning;
    private bool _spinning;
    private HomeViewModel? _observedViewModel;

    public BrandLogoView()
    {
        InitializeComponent();
#if WINDOWS
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
#if WINDOWS
        return;
#else
        if (e.PropertyName is null or nameof(HomeViewModel.IsContentLoading) or nameof(HomeViewModel.HasError))
        {
            ApplyLoadState();
        }
#endif
    }

    /// <summary>
    /// Spin while data loads / the UI gets ready; a surfaced service error
    /// with no rows rests the crest instead of spinning forever.
    /// </summary>
    private bool ShouldSpin => _observedViewModel is { IsContentLoading: true, HasError: false };

    private void ApplyLoadState()
    {
        if (!IsLoaded) return;
#if WINDOWS
        return;
#endif

        if (ShouldSpin)
        {
            StartLoadingSpin();
        }
        else
        {
            SettleFromSpin();
        }
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        CrestImage.Source ??= BrandCrestImageLoader.GetCrest();
        DisableFocusTraversal();

#if WINDOWS
        // No spin/sheen: aborting the loading animation when the catalog
        // lands was enough to 0xc000027b on WinAppSDK 1.8.
        ResetSpinVisuals();
        return;
#endif

        if (ShouldSpin)
        {
            StartLoadingSpin();
        }
        else
        {
            // Opening sheen sweep; the ambient shimmer loop starts when it lands.
            RunSheenSweep(0.85);
        }
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _ambientRunning = false;
        _spinning = false;
        this.AbortAnimation(SheenAnimation);
        this.AbortAnimation(AmbientAnimation);
        this.AbortAnimation(SpinAnimation);
        this.AbortAnimation(SettleAnimation);
        ResetSpinVisuals();
    }

    /// <summary>
    /// Decoration only: the crest (and everything inside it) must be skipped
    /// by D-pad traversal. The Leagues/menu button to its right stays focusable.
    /// </summary>
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

    /// <summary>TV focus entered the header area: subtle scale + glow + sheen.</summary>
    public void OnHeaderFocusEntered()
    {
#if WINDOWS
        return;
#endif
        _ = LogoOuter.ScaleToAsync(1.08, FocusScaleMs, Easing.CubicOut);
        _ = GlowRing.FadeToAsync(0.7, FocusScaleMs);
        RunSheenSweep(0.85);
    }

    public void OnHeaderFocusExited()
    {
#if WINDOWS
        return;
#endif
        _ = LogoOuter.ScaleToAsync(1.0, FocusScaleMs, Easing.CubicOut);
        _ = GlowRing.FadeToAsync(0.0, FocusScaleMs);
    }

    // ---------------------------------------------------------- loading spin --

    private void StartLoadingSpin()
    {
        if (_spinning) return;
        _spinning = true;

        _ambientRunning = false;
        this.AbortAnimation(AmbientAnimation);
        this.AbortAnimation(SheenAnimation);
        this.AbortAnimation(SettleAnimation);

#if WINDOWS
        // Opacity pulse only: RotationY/Rotation during catalog Dispatch is a
        // WinUI 1.8 CoreMessaging 0xc000027b. Same "we're loading" signal.
        var pulse = new Animation();
        pulse.Add(0.0, 0.5, new Animation(v => LogoOuter.Opacity = v, 1.0, 0.55, Easing.SinInOut));
        pulse.Add(0.5, 1.0, new Animation(v => LogoOuter.Opacity = v, 0.55, 1.0, Easing.SinInOut));
        pulse.Commit(this, SpinAnimation, length: 1400, repeat: () => _spinning);
        return;
#endif

        var spin = new Animation(ApplySpinFrame, 0, 360);
        spin.Commit(this, SpinAnimation, length: SpinTurnMs, easing: Easing.Linear, repeat: () => _spinning);
    }

    /// <summary>Rows are ready: ease to rest over the shortest arc, then sheen-sweep.</summary>
    private void SettleFromSpin()
    {
        if (!_spinning) return;
        _spinning = false;
        this.AbortAnimation(SpinAnimation);
#if WINDOWS
        ResetSpinVisuals();
        if (IsLoaded)
        {
            RunSheenSweep(0.85);
        }
        return;
#endif

        var current = LogoOuter.RotationY % 360;
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

    /// <summary>
    /// One turntable frame — transform/opacity only, all derived from the same
    /// rotation angle:
    /// the face compresses toward edge-on (RotationY perspective), the dark
    /// rim widens/darkens and drifts toward the receding side like a turning
    /// coin's edge, and the specular glint travels across the face, brightest
    /// near the 45° sweet-spot angles and gone when edge-on.
    /// </summary>
    private void ApplySpinFrame(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var sin = Math.Sin(radians);
        var absSin = Math.Abs(sin);
        var faceVisibility = Math.Abs(Math.Cos(radians));

#if WINDOWS
        // 2D spin: same "loading" signal, no WinUI PlaneProjection.
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

    // ------------------------------------------------------------ rest state --

    private double SheenTravel => Math.Max(Width, 40) + 26;

    private void RunSheenSweep(double peakOpacity)
    {
        // The spin owns the sheen while it runs (the glint is a spin-frame
        // output); a header-focus sweep during loading would fight it.
        if (_spinning) return;

        // One animation owns the sheen at a time: pause the ambient loop for
        // the sweep and resume it when the sweep lands.
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

    /// <summary>
    /// Ambient shimmer: a low-opacity sheen crosses the crest during the first
    /// quarter of a 6s loop, then the crest rests. Opacity/translation only.
    /// </summary>
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
