namespace VardyParty.HomeUi.Views;

/// <summary>
/// The shared homepage surface. Embed it in any host page (the Desktop head's
/// <see cref="HomePage"/>, the MAUI head's HomeHostPage) with a
/// <see cref="HomeViewModel"/> BindingContext; hosts push games into the view
/// model and handle <see cref="HomeViewModel.GamePicked"/>.
/// </summary>
public partial class HomeView : ContentView
{
    private IDispatcherTimer? _applyPump;

    public HomeView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private HomeViewModel? ViewModel => BindingContext as HomeViewModel;

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_applyPump != null) return;

        // WinAppSDK stows 0xc000027b if catalog apply is Dispatcher.Dispatch'd
        // from the Rx/HTTP thread. Drain the pending queue on this view's
        // UI-thread timer instead — same Rows binding on every platform.
        _applyPump = Dispatcher.CreateTimer();
        _applyPump.Interval = TimeSpan.FromMilliseconds(50);
        _applyPump.Tick += OnApplyPumpTick;
        _applyPump.Start();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        if (_applyPump == null) return;
        _applyPump.Stop();
        _applyPump.Tick -= OnApplyPumpTick;
        _applyPump = null;
    }

    private void OnApplyPumpTick(object? sender, EventArgs e) =>
        ViewModel?.FlushPendingApply();

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        ViewModel?.SetViewport(Width, Height, IsTelevision());
    }

    internal static bool IsTelevision()
    {
        try
        {
            return DeviceInfo.Current.Idiom == DeviceIdiom.TV;
        }
        catch
        {
            return false;
        }
    }

    private void OnMenuClicked(object? sender, EventArgs e) => ViewModel?.ToggleMenu();

    private void OnCloseMenuClicked(object? sender, EventArgs e) => ViewModel?.CloseMenu();

    private void OnScrimTapped(object? sender, TappedEventArgs e) => ViewModel?.CloseMenu();

    private void OnShowAllClicked(object? sender, EventArgs e) => ViewModel?.ShowAllLeagues();

    private void OnResetClicked(object? sender, EventArgs e) => ViewModel?.ResetLeaguesToDefaults();

    private void OnSignOutClicked(object? sender, EventArgs e) => ViewModel?.RequestSignOut();

    private void OnMenuItemFocused(object? sender, FocusEventArgs e)
    {
        ViewModel?.OnFocusPulse();

        if (ReferenceEquals(sender, MenuButton))
        {
            BrandLogo.OnHeaderFocusEntered();
        }
    }

    private void OnHeaderUnfocused(object? sender, FocusEventArgs e) => BrandLogo.OnHeaderFocusExited();
}
