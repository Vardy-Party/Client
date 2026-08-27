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
    private HomeViewModel? _wiredViewModel;

    public HomeView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private HomeViewModel? ViewModel => BindingContext as HomeViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        WireViewModel(ViewModel);
    }

    private void WireViewModel(HomeViewModel? vm)
    {
        if (ReferenceEquals(_wiredViewModel, vm))
        {
            return;
        }

        if (_wiredViewModel != null)
        {
            _wiredViewModel.WorkQueued -= OnWorkQueued;
        }

        _wiredViewModel = vm;
        if (_wiredViewModel != null)
        {
            _wiredViewModel.WorkQueued += OnWorkQueued;
        }
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        WireViewModel(ViewModel);

        if (_applyPump != null)
        {
            return;
        }

        // WinAppSDK stows 0xc000027b if catalog apply is Dispatcher.Dispatch'd
        // from the Rx/HTTP thread. Drain the pending queue on this view's
        // UI-thread timer instead — same Rows binding on every platform.
        // The timer runs only while work is queued (TV should not tick at 20 Hz idle).
        _applyPump = Dispatcher.CreateTimer();
        _applyPump.Interval = TimeSpan.FromMilliseconds(50);
        _applyPump.Tick += OnApplyPumpTick;
        if (ViewModel?.HasPendingWork == true)
        {
            _applyPump.Start();
        }
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        WireViewModel(null);

        if (_applyPump == null)
        {
            return;
        }

        _applyPump.Stop();
        _applyPump.Tick -= OnApplyPumpTick;
        _applyPump = null;
    }

    private void OnWorkQueued()
    {
        if (Dispatcher.IsDispatchRequired)
        {
            _ = Dispatcher.Dispatch(EnsureApplyPumpRunning);
            return;
        }

        EnsureApplyPumpRunning();
    }

    private void EnsureApplyPumpRunning()
    {
        if (_applyPump is not { IsRunning: false })
        {
            return;
        }

        _applyPump.Start();
    }

    private void OnApplyPumpTick(object? sender, EventArgs e)
    {
        ViewModel?.FlushPendingApply();
        if (ViewModel?.HasPendingWork != true)
        {
            _applyPump?.Stop();
        }
    }

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
