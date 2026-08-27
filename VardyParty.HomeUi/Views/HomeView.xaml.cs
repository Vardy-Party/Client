namespace VardyParty.HomeUi.Views;

/// <summary>
/// The shared homepage surface. Embed it in any host page (the Desktop head's
/// <see cref="HomePage"/>, the MAUI head's HomeHostPage) with a
/// <see cref="HomeViewModel"/> BindingContext; hosts push games into the view
/// model and handle <see cref="HomeViewModel.GamePicked"/>. Catalog apply is
/// owned here: Windows drains a UI timer; Android/Desktop flush on MainThread.
/// </summary>
public partial class HomeView : ContentView
{
#if WINDOWS
    private IDispatcherTimer? _applyPump;
#endif
    private HomeViewModel? _wiredViewModel;

    public HomeView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
#if WINDOWS
        // Deferred crest work (spin restart after a layout abort) rides the
        // same UI-thread pump as the catalog apply — never Dispatcher.Dispatch.
        BrandLogo.PumpRequested += QueuePumpStart;
#endif
    }

    private HomeViewModel? ViewModel => BindingContext as HomeViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        WireViewModel(ViewModel);
        DrainIfPending();
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
        DrainIfPending();
    }

    private void OnUnloaded(object? sender, EventArgs e)
    {
        // Keep WorkQueued wired: Android handler reconnect Unloads this view
        // without a later Loaded. The catalog must still be able to drain.
#if WINDOWS
        StopApplyPump();
#endif
    }

    private void OnWorkQueued() => DrainIfPending();

    private void DrainIfPending()
    {
        if (ViewModel?.HasPendingWork != true)
        {
            return;
        }

#if WINDOWS
        // WinAppSDK 1.8 stows 0xc000027b if catalog apply is Dispatcher.Dispatch'd
        // from the Rx/HTTP thread into WinUI layout. A UI-thread timer drains instead.
        QueuePumpStart();
#else
        // Android TV (and Desktop): IDispatcherTimer often never ticks while the
        // main thread is skipping hundreds of frames at startup. Flush on the
        // platform main thread — that is not a WinUI layout Dispatch.
        if (MainThread.IsMainThread)
        {
            ViewModel.FlushPendingApply();
            BrandLogo.OnCatalogApplied();
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            ViewModel?.FlushPendingApply();
            BrandLogo.OnCatalogApplied();
        });
#endif
    }

#if WINDOWS
    private void QueuePumpStart()
    {
        if (!Dispatcher.IsDispatchRequired)
        {
            EnsureApplyPumpRunning();
            return;
        }

        if (!Dispatcher.Dispatch(EnsureApplyPumpRunning))
        {
            MainThread.BeginInvokeOnMainThread(EnsureApplyPumpRunning);
        }
    }

    private void EnsureApplyPumpRunning()
    {
        if (_applyPump == null)
        {
            _applyPump = Dispatcher.CreateTimer();
            _applyPump.Interval = TimeSpan.FromMilliseconds(50);
            _applyPump.Tick += OnApplyPumpTick;
        }

        if (ViewModel?.HasPendingWork != true && !BrandLogo.HasPendingCrestWork)
        {
            return;
        }

        if (!_applyPump.IsRunning)
        {
            _applyPump.Start();
        }
    }

    private void StopApplyPump()
    {
        if (_applyPump == null)
        {
            return;
        }

        _applyPump.Stop();
        _applyPump.Tick -= OnApplyPumpTick;
        _applyPump = null;
    }

    private void OnApplyPumpTick(object? sender, EventArgs e)
    {
        ViewModel?.FlushPendingApply();
        BrandLogo.OnCatalogApplied();
        BrandLogo.PumpCrest();
        if (ViewModel?.HasPendingWork != true && !BrandLogo.HasPendingCrestWork)
        {
            _applyPump?.Stop();
        }
    }
#endif

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
