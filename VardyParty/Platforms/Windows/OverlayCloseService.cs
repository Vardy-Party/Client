using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VardyParty.Services;
using Button = Microsoft.UI.Xaml.Controls.Button;
using Grid = Microsoft.UI.Xaml.Controls.Grid;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using Thickness = Microsoft.UI.Xaml.Thickness;
using VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using Window = Microsoft.UI.Xaml.Window;

namespace VardyParty.Platforms.Windows;

public class OverlayCloseService : IOverlayCloseService
{
    private readonly IStreamSwitchingService _switching;
    private readonly ILogger<OverlayCloseService> _logger;
    private Button? _closeButton;
    public event Action? CloseRequested;

    public OverlayCloseService(IStreamSwitchingService switching, ILogger<OverlayCloseService> logger)
    {
        _switching = switching;
        _logger = logger;
    }

    public void ShowCloseControl()
    {
        try
        {
            var mauiWindow = Application.Current?.Windows?.FirstOrDefault();
            var nativeWindow = mauiWindow?.Handler?.PlatformView as Window;
            if (nativeWindow == null) return;

            var dq = nativeWindow.DispatcherQueue;
            dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                try
                {
                    // Ensure Content is a Panel we can add into
                    Panel panel;
                    var existing = nativeWindow.Content;
                    if (nativeWindow.Content is Panel asPanel)
                    {
                        panel = asPanel;
                    }
                    else
                    {
                        var grid = new Grid();
                        if (existing != null) grid.Children.Add(existing);
                        nativeWindow.Content = grid;
                        panel = grid;
                    }

                    // Avoid adding multiple buttons
                    if (panel.Children.OfType<Button>().Any(b => b.Name == "VardyOverlayClose")) return;

                    _closeButton = new Button
                    {
                        Name = "VardyOverlayClose",
                        Content = "?",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(12),
                        Width = 48,
                        Height = 48
                    };

                    _closeButton.Click += (s, e) =>
                    {
                        try
                        {
                            _switching.Cleanup();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[OverlayClose] Cleanup failed");
                        }

                        try
                        {
                            CloseRequested?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "[OverlayClose] CloseRequested failed");
                        }
                    };

                    panel.Children.Add(_closeButton);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[OverlayClose] ShowCloseControl enqueue failed");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[OverlayClose] ShowCloseControl failed");
        }
    }

    public void HideCloseControl()
    {
        try
        {
            var mauiWindow = Application.Current?.Windows?.FirstOrDefault();
            var nativeWindow = mauiWindow?.Handler?.PlatformView as Window;
            if (nativeWindow == null) return;

            var dq = nativeWindow.DispatcherQueue;
            dq.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                try
                {
                    if (_closeButton != null)
                    {
                        var parent = VisualTreeHelper.GetParent(_closeButton) as Panel;
                        if (parent != null) parent.Children.Remove(_closeButton);
                        _closeButton = null;
                    }
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }
}
