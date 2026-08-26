namespace VardyParty.HomeUi.Views;

/// <summary>
/// The 3D metallic Vardy Party crest in the homepage header. Follows the same
/// performance discipline as the match cards: all animation is opacity and
/// transform only (a sheen sweep on load, a slow ambient shimmer loop, and a
/// subtle scale/glow response when TV focus enters the header), everything
/// aborted on unload so nothing runs per-frame on the CPU while off screen.
/// </summary>
public partial class BrandLogoView : ContentView
{
    private const string SheenAnimation = "BrandSheenSweep";
    private const string AmbientAnimation = "BrandAmbientShimmer";
    private const uint FocusScaleMs = 130;
    private const double AmbientOpacity = 0.35;

    private bool _ambientRunning;

    public BrandLogoView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        CrestImage.Source ??= BrandCrestImageLoader.GetCrest();

        // Opening sheen sweep; the ambient shimmer loop starts when it lands.
        RunSheenSweep(0.85);
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        _ambientRunning = false;
        this.AbortAnimation(SheenAnimation);
        this.AbortAnimation(AmbientAnimation);
    }

    /// <summary>TV focus entered the header area: subtle scale + glow + sheen.</summary>
    public void OnHeaderFocusEntered()
    {
        _ = LogoOuter.ScaleToAsync(1.08, FocusScaleMs, Easing.CubicOut);
        _ = GlowRing.FadeToAsync(0.7, FocusScaleMs);
        RunSheenSweep(0.85);
    }

    /// <summary>TV focus left the header area.</summary>
    public void OnHeaderFocusExited()
    {
        _ = LogoOuter.ScaleToAsync(1.0, FocusScaleMs, Easing.CubicOut);
        _ = GlowRing.FadeToAsync(0.0, FocusScaleMs);
    }

    private double SheenTravel => Math.Max(Width, 40) + 26;

    private void RunSheenSweep(double peakOpacity)
    {
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
