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
#if WINDOWS
        BindingContextChanged += OnBindingContextChanged;
#endif
    }

    private HomeViewModel? ViewModel => BindingContext as HomeViewModel;

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_applyPump != null) return;

        _applyPump = Dispatcher.CreateTimer();
#if WINDOWS
        _applyPump.Interval = TimeSpan.FromMilliseconds(200);
#else
        _applyPump.Interval = TimeSpan.FromMilliseconds(50);
#endif
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

    private void OnApplyPumpTick(object? sender, EventArgs e)
    {
        ViewModel?.FlushPendingApply();
#if WINDOWS
        SyncWindowsCatalog();
#endif
    }

#if WINDOWS
    private HorizontalStackLayout? _windowsRowCards;
    private int _windowsSyncedCount;
    private readonly List<(string League, Image Icon)> _windowsHeaderIcons = new();

    private void OnBindingContextChanged(object? sender, EventArgs e) => SyncWindowsCatalog();

    private void SyncWindowsCatalog()
    {
        if (WindowsCatalogHost == null || ViewModel == null) return;

        var lines = ViewModel.WindowsPaintedLines;
        if (_windowsSyncedCount > lines.Count)
        {
            WindowsCatalogHost.Children.Clear();
            _windowsRowCards = null;
            _windowsSyncedCount = 0;
            _windowsHeaderIcons.Clear();
        }

        ApplyWindowsLeagueIcons();

        if (_windowsSyncedCount >= lines.Count) return;

        var item = lines[_windowsSyncedCount];
        _windowsSyncedCount++;

        if (item.Card == null)
        {
            var header = new HorizontalStackLayout
            {
                Spacing = 10,
                Margin = new Thickness(0, 12, 0, 6),
            };
            var icon = new Image
            {
                WidthRequest = ViewModel.Layout.LeagueIconSize,
                HeightRequest = ViewModel.Layout.LeagueIconSize,
                Aspect = Aspect.AspectFit,
                VerticalOptions = LayoutOptions.Center,
                IsVisible = false,
            };
            header.Children.Add(icon);
            header.Children.Add(new Label
            {
                Text = item.Header ?? string.Empty,
                TextColor = Colors.White,
                FontAttributes = FontAttributes.Bold,
                FontSize = 18,
                VerticalOptions = LayoutOptions.Center,
            });
            if (item.HeaderIsLive)
            {
                header.Children.Add(new Border
                {
                    Padding = new Thickness(8, 2),
                    BackgroundColor = Color.FromArgb("#8CBE1233"),
                    StrokeThickness = 0,
                    VerticalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = "LIVE",
                        FontSize = 11,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Color.FromArgb("#FECACA"),
                    },
                });
            }

            if (!string.IsNullOrEmpty(item.Header))
            {
                _windowsHeaderIcons.Add((item.Header, icon));
            }

            WindowsCatalogHost.Children.Add(header);
            _windowsRowCards = new HorizontalStackLayout { Spacing = ViewModel.Layout.CardSpacing };
            WindowsCatalogHost.Children.Add(CreateWindowsCardRow(_windowsRowCards));
            ApplyWindowsLeagueIcons();
            return;
        }

        _windowsRowCards ??= new HorizontalStackLayout { Spacing = ViewModel.Layout.CardSpacing };
        if (_windowsRowCards.Parent == null)
        {
            WindowsCatalogHost.Children.Add(CreateWindowsCardRow(_windowsRowCards));
        }

        _windowsRowCards.Children.Add(new MatchCardView { BindingContext = item.Card });
    }

    private View CreateWindowsCardRow(HorizontalStackLayout cards)
    {
        var layout = ViewModel!.Layout;
        var inset = Math.Ceiling(layout.CardWidth * 0.09 / 2.0) + 24;
        cards.Spacing = layout.CardSpacing;
        // Grid padding (not a nested ScrollView): WinUI StackPanel padding is
        // unreliable, and an inner horizontal ScrollViewer steals mouse-drag
        // from the homepage.
        return new Grid
        {
            Padding = new Thickness(inset, inset * 0.5, inset, inset * 0.5),
            HeightRequest = layout.RowHeight + inset,
            Children = { cards },
        };
    }

    private void ApplyWindowsLeagueIcons()
    {
        if (ViewModel == null) return;
        foreach (var (league, icon) in _windowsHeaderIcons)
        {
            if (icon.Source != null) continue;
            var src = ViewModel.TryGetWindowsLeagueIcon(league);
            if (src == null) continue;
            icon.Source = src;
            icon.IsVisible = true;
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
            // Essentials may be unavailable on some drawn backends; assume not a TV.
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

        // The menu button is the header's focusable element: entering it means
        // TV focus reached the header area, so the brand crest responds.
        if (ReferenceEquals(sender, MenuButton))
        {
            BrandLogo.OnHeaderFocusEntered();
        }
    }

    private void OnHeaderUnfocused(object? sender, FocusEventArgs e) => BrandLogo.OnHeaderFocusExited();
}
