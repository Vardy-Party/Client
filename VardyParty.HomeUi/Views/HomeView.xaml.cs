namespace VardyParty.HomeUi.Views;

/// <summary>
/// The shared homepage surface. Embed it in any host page (the Desktop head's
/// <see cref="HomePage"/>, the MAUI head's HomeHostPage) with a
/// <see cref="HomeViewModel"/> BindingContext; hosts push games into the view
/// model and handle <see cref="HomeViewModel.GamePicked"/>.
/// </summary>
public partial class HomeView : ContentView
{
    public HomeView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private HomeViewModel? ViewModel => BindingContext as HomeViewModel;

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

    private void OnMenuItemFocused(object? sender, FocusEventArgs e) => ViewModel?.OnFocusPulse();
}
