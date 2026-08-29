namespace VardyParty.HomeUi.Views;

/// <summary>
/// Thin page wrapper around <see cref="HomeView"/> for heads without extra
/// chrome (the Desktop preview head). The MAUI head hosts HomeView inside its
/// own page so it can layer auth and stream-resolution overlays on top.
/// Hosts push games into <see cref="HomeViewModel"/> and handle
/// <see cref="HomeViewModel.GamePicked"/>; this page only renders.
/// </summary>
public partial class HomePage : ContentPage
{
    public HomePage(HomeViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
