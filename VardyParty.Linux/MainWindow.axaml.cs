using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;
using Avalonia.Platform;
using Avalonia.Controls.Platform;
using System.Runtime.InteropServices;
using Avalonia.Interactivity;

namespace VardyParty.Linux;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
        this.AttachedToVisualTree += (_, _) => TrySetVideoSurfaceHandle();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void TrySetVideoSurfaceHandle()
    {
        // Try to wire up the video surface handle to the player service
        if (DataContext is MainWindowViewModel vm)
        {
            var handle = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
            {
                vm.SetVideoSurfaceHandle(handle);
            }
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private async void OnGamesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            await vm.PlaySelectedGameAsync(vm.SelectedGame);
        }
    }

    private void OnCloseVideoClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.CloseVideoPlayback();
        }
    }
}
