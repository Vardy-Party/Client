using VardyParty.Kernel;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// The shared homepage. Hosts push games into <see cref="HomeViewModel"/> and
/// handle <see cref="HomeViewModel.GamePicked"/>; this page only renders.
/// </summary>
public partial class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;

    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;

        SizeChanged += OnSizeChanged;
        _viewModel.GamePicked += OnGamePicked;
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        _viewModel.SetViewport(Width, Height, IsTelevision());
    }

    private static bool IsTelevision()
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

    private void OnGamePicked(Game game)
    {
        // Stream resolution + playback wiring is a per-head concern; the
        // desktop preview head confirms the pick until playback lands there.
        _ = DisplayAlertAsync(
            "Match selected",
            $"{game.DisplayHome} v {game.DisplayAway}\n\nPlayback is not wired into this preview head yet.",
            "OK");
    }

    private void OnMenuClicked(object? sender, EventArgs e) => _viewModel.ToggleMenu();

    private void OnCloseMenuClicked(object? sender, EventArgs e) => _viewModel.CloseMenu();

    private void OnScrimTapped(object? sender, TappedEventArgs e) => _viewModel.CloseMenu();

    private void OnShowAllClicked(object? sender, EventArgs e) => _viewModel.ShowAllLeagues();

    private void OnResetClicked(object? sender, EventArgs e) => _viewModel.ResetLeaguesToDefaults();
}
