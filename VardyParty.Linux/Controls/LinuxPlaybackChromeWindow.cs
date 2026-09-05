using Avalonia.Input;
using Avalonia.Threading;
using VardyParty.Presentation;
using AvBorder = Avalonia.Controls.Border;
using AvButton = Avalonia.Controls.Button;
using AvColor = Avalonia.Media.Color;
using AvCornerRadius = Avalonia.CornerRadius;
using AvDock = Avalonia.Controls.Dock;
using AvDockPanel = Avalonia.Controls.DockPanel;
using AvFontWeight = Avalonia.Media.FontWeight;
using AvHorizontalAlignment = Avalonia.Layout.HorizontalAlignment;
using AvOrientation = Avalonia.Layout.Orientation;
using AvPanel = Avalonia.Controls.Panel;
using AvPixelRect = Avalonia.PixelRect;
using AvSolidColorBrush = Avalonia.Media.SolidColorBrush;
using AvStackPanel = Avalonia.Controls.StackPanel;
using AvWindowDecorations = Avalonia.Controls.WindowDecorations;
using AvTextBlock = Avalonia.Controls.TextBlock;
using AvTextTrimming = Avalonia.Media.TextTrimming;
using AvTextWrapping = Avalonia.Media.TextWrapping;
using AvThickness = Avalonia.Thickness;
using AvVerticalAlignment = Avalonia.Layout.VerticalAlignment;
using AvWindow = Avalonia.Controls.Window;
using AvWindowStartupLocation = Avalonia.Controls.WindowStartupLocation;
using AvWindowTransparencyLevel = Avalonia.Controls.WindowTransparencyLevel;
using AvBrushes = Avalonia.Media.Brushes;

namespace VardyParty.Linux.Controls;

/// <summary>
/// Transparent Avalonia overlay window drawn above the LibVLC native child
/// (airspace). MAUI cannot paint on the video surface; this window hosts
/// menu / video-info / stream toast / scores / next chrome driven by
/// <see cref="PlaybackChromePresenter"/>. Close + match-event toast stay in
/// the reserved MAUI airspace row on <c>LinuxHomePage</c>.
/// </summary>
public sealed class LinuxPlaybackChromeWindow : AvWindow
{
    private readonly PlaybackChromePresenter _chrome;
    private readonly AvButton _menuButton;
    private readonly AvStackPanel _menuPanel;
    private readonly AvTextBlock _reportStatus;
    private readonly AvButton _nextMenuButton;
    private readonly AvBorder _infoPanel;
    private readonly AvTextBlock _infoText;
    private readonly AvBorder _streamToast;
    private readonly AvTextBlock _streamToastText;
    private readonly AvBorder _sourceBadge;
    private readonly AvTextBlock _sourceBadgeText;
    private readonly AvBorder _scoresBar;
    private readonly AvTextBlock _scoresText;
    private readonly AvButton _nextButton;
    private readonly AvTextBlock _nextHint;
    private readonly AvBorder _nextHost;
    private readonly AvPanel _dismissSurface;

    private DispatcherTimer? _toastHideTimer;
    private string _videoInfoBody = string.Empty;

    public LinuxPlaybackChromeWindow(PlaybackChromePresenter chrome)
    {
        _chrome = chrome;

        Title = "Vardy Party playback chrome";
        WindowDecorations = AvWindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        TransparencyLevelHint = new[] { AvWindowTransparencyLevel.Transparent };
        TransparencyBackgroundFallback = new AvSolidColorBrush(AvColor.FromArgb(1, 0, 0, 0));
        Background = AvBrushes.Transparent;
        WindowStartupLocation = AvWindowStartupLocation.Manual;

        _menuButton = MakeChromeButton("☰", 42, 42);
        _menuButton.HorizontalAlignment = AvHorizontalAlignment.Left;
        _menuButton.VerticalAlignment = AvVerticalAlignment.Top;
        _menuButton.Margin = new AvThickness(12, 12, 0, 0);

        _menuPanel = new AvStackPanel
        {
            Orientation = AvOrientation.Vertical,
            Spacing = 2,
            MinWidth = 180,
            Margin = new AvThickness(12, 62, 0, 0),
            Background = new AvSolidColorBrush(AvColor.FromArgb(0xF2, 0x18, 0x18, 0x18)),
            IsVisible = false,
            HorizontalAlignment = AvHorizontalAlignment.Left,
            VerticalAlignment = AvVerticalAlignment.Top
        };
        _menuPanel.Children.Add(MakeMenuItem("Video Info", () => _chrome.ToggleVideoInfo()));
        _menuPanel.Children.Add(MakeMenuItem("Scores", () =>
        {
            _chrome.ToggleScores();
            RefreshScoresVisibility();
        }));
        _menuPanel.Children.Add(MakeMenuItem("Report stream", () => _ = ReportAsync()));
        _nextMenuButton = MakeMenuItem("Next", () => _ = _chrome.RequestNextStreamAsync());
        _menuPanel.Children.Add(_nextMenuButton);
        _menuPanel.Children.Add(MakeMenuItem("Exit", () => _chrome.Exit()));
        _reportStatus = new AvTextBlock
        {
            Foreground = new AvSolidColorBrush(AvColor.FromRgb(0xFF, 0xB3, 0x00)),
            FontSize = 12,
            Margin = new AvThickness(12, 2, 12, 4),
            IsVisible = false
        };
        _menuPanel.Children.Add(_reportStatus);

        _infoText = new AvTextBlock
        {
            Foreground = AvBrushes.White,
            FontSize = 13,
            TextWrapping = AvTextWrapping.Wrap
        };
        var infoClose = MakeChromeButton("✕", 32, 32);
        infoClose.HorizontalAlignment = AvHorizontalAlignment.Right;
        infoClose.Click += (_, _) => _chrome.HideVideoInfo();
        var infoStack = new AvDockPanel { LastChildFill = true };
        AvDockPanel.SetDock(infoClose, AvDock.Top);
        infoStack.Children.Add(infoClose);
        infoStack.Children.Add(_infoText);
        _infoPanel = new AvBorder
        {
            Background = new AvSolidColorBrush(AvColor.FromArgb(0xE6, 0x10, 0x10, 0x10)),
            CornerRadius = new AvCornerRadius(8),
            Padding = new AvThickness(14),
            Margin = new AvThickness(64, 64, 64, 64),
            MaxWidth = 520,
            HorizontalAlignment = AvHorizontalAlignment.Center,
            VerticalAlignment = AvVerticalAlignment.Center,
            IsVisible = false,
            Child = infoStack
        };

        _streamToastText = new AvTextBlock
        {
            Foreground = AvBrushes.White,
            FontSize = 14,
            VerticalAlignment = AvVerticalAlignment.Center
        };
        _sourceBadgeText = new AvTextBlock { FontSize = 11, FontWeight = AvFontWeight.SemiBold };
        _sourceBadge = new AvBorder
        {
            CornerRadius = new AvCornerRadius(4),
            Padding = new AvThickness(6, 2, 6, 2),
            Margin = new AvThickness(8, 0, 0, 0),
            IsVisible = false,
            Child = _sourceBadgeText
        };
        var toastRow = new AvStackPanel
        {
            Orientation = AvOrientation.Horizontal,
            Spacing = 0,
            Children = { _streamToastText, _sourceBadge }
        };
        _streamToast = new AvBorder
        {
            Background = new AvSolidColorBrush(AvColor.FromArgb(0xCC, 0x10, 0x10, 0x10)),
            CornerRadius = new AvCornerRadius(6),
            Padding = new AvThickness(12, 8, 12, 8),
            Margin = new AvThickness(0, 16, 0, 0),
            HorizontalAlignment = AvHorizontalAlignment.Center,
            VerticalAlignment = AvVerticalAlignment.Top,
            IsVisible = false,
            Child = toastRow
        };

        _scoresText = new AvTextBlock
        {
            Foreground = AvBrushes.White,
            FontSize = 13,
            TextTrimming = AvTextTrimming.CharacterEllipsis,
            VerticalAlignment = AvVerticalAlignment.Center
        };
        _scoresBar = new AvBorder
        {
            Background = new AvSolidColorBrush(AvColor.FromArgb(0xCC, 0x00, 0x00, 0x00)),
            Padding = new AvThickness(12, 8, 12, 8),
            MinHeight = 36,
            VerticalAlignment = AvVerticalAlignment.Bottom,
            HorizontalAlignment = AvHorizontalAlignment.Stretch,
            IsVisible = false,
            Child = _scoresText
        };
        _scoresBar.PointerPressed += (_, e) =>
        {
            _chrome.CycleScoresMode();
            e.Handled = true;
        };

        _nextButton = MakeChromeButton("⏭", 48, 48);
        _nextHint = new AvTextBlock
        {
            Foreground = AvBrushes.White,
            FontSize = 12,
            HorizontalAlignment = AvHorizontalAlignment.Center,
            Opacity = 0.85
        };
        var nextStack = new AvStackPanel
        {
            Orientation = AvOrientation.Vertical,
            Spacing = 4,
            Children = { _nextButton, _nextHint }
        };
        _nextHost = new AvBorder
        {
            Background = AvBrushes.Transparent,
            Padding = new AvThickness(24),
            HorizontalAlignment = AvHorizontalAlignment.Right,
            VerticalAlignment = AvVerticalAlignment.Center,
            Margin = new AvThickness(0, 0, 8, 0),
            IsVisible = false,
            Opacity = 0,
            Child = nextStack
        };
        _nextHost.PointerEntered += (_, _) => _nextHost.Opacity = 1;
        _nextHost.PointerExited += (_, _) => _nextHost.Opacity = 0;
        _nextButton.Click += async (_, _) => await _chrome.RequestNextStreamAsync();

        _dismissSurface = new AvPanel
        {
            Background = AvBrushes.Transparent,
            IsVisible = false
        };
        _dismissSurface.PointerPressed += (_, e) =>
        {
            _chrome.HideMenu();
            _chrome.HideVideoInfo();
            e.Handled = true;
        };

        _menuButton.Click += (_, _) => _chrome.ToggleMenu();

        var root = new AvPanel();
        root.Children.Add(_dismissSurface);
        root.Children.Add(_streamToast);
        root.Children.Add(_scoresBar);
        root.Children.Add(_nextHost);
        root.Children.Add(_infoPanel);
        root.Children.Add(_menuPanel);
        root.Children.Add(_menuButton);
        Content = root;

        KeyDown += OnKeyDown;
        _chrome.StateChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyState);
        _chrome.StreamToastRequested += (_, toast) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowToast(toast));
        _chrome.StreamToastDismissed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(HideToast);

        ApplyState();
    }

    public void SetVideoInfoBody(string body)
    {
        _videoInfoBody = body ?? string.Empty;
        if (_chrome.IsVideoInfoVisible)
            _infoText.Text = _videoInfoBody;
    }

    public void SetScoresText(string text) => _scoresText.Text = text ?? string.Empty;

    public void SetSourceBadge(string? label)
    {
        var style = Kernel.SourceBadgeStyle.ForLabel(label);
        if (style is null || string.IsNullOrWhiteSpace(label))
        {
            _sourceBadge.IsVisible = false;
            return;
        }

        _sourceBadgeText.Text = label;
        _sourceBadge.Background = new AvSolidColorBrush(AvColor.FromArgb(
            style.Value.BgA, style.Value.BgR, style.Value.BgG, style.Value.BgB));
        _sourceBadgeText.Foreground = new AvSolidColorBrush(AvColor.FromArgb(
            style.Value.FgA, style.Value.FgR, style.Value.FgG, style.Value.FgB));
        _sourceBadge.IsVisible = true;
    }

    public void PlaceOver(AvPixelRect screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
            return;

        Position = screenBounds.Position;
        Width = screenBounds.Width;
        Height = screenBounds.Height;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (_chrome.TryDismissLayer())
        {
            e.Handled = true;
            return;
        }

        _chrome.Exit();
        e.Handled = true;
    }

    private async Task ReportAsync()
    {
        await _chrome.ReportBadStreamAsync();
        try
        {
            await Task.Delay(PlaybackChromePresenter.ReportStatusLinger);
        }
        catch
        {
            // ignore
        }

        _chrome.ClearReportStatus();
        _chrome.HideMenu();
    }

    private void ApplyState()
    {
        _menuPanel.IsVisible = _chrome.IsMenuVisible;
        _infoPanel.IsVisible = _chrome.IsVideoInfoVisible;
        if (_chrome.IsVideoInfoVisible)
            _infoText.Text = _videoInfoBody;

        _dismissSurface.IsVisible = _chrome.IsMenuVisible || _chrome.IsVideoInfoVisible;
        _nextMenuButton.IsEnabled = _chrome.CanGoNext;
        _nextMenuButton.Opacity = _chrome.CanGoNext ? 1 : 0.45;

        if (_chrome.ReportState == PlaybackReportUiState.Idle)
        {
            _reportStatus.IsVisible = false;
        }
        else
        {
            _reportStatus.Text = _chrome.ReportStatusText;
            _reportStatus.IsVisible = true;
        }

        if (!_chrome.IsStreamToastVisible)
            HideToast();

        RefreshScoresVisibility();
        RefreshNextVisibility();
    }

    private void RefreshScoresVisibility()
    {
        _scoresBar.IsVisible = _chrome.IsScoresVisible;
    }

    private void RefreshNextVisibility()
    {
        var show = _chrome.CanGoNext;
        _nextHost.IsVisible = show;
        if (!show)
            _nextHost.Opacity = 0;

        if (_chrome.StreamToast is { } toast)
            _nextHint.Text = $"{toast.Index}/{toast.Total}";
        else
            _nextHint.Text = string.Empty;
    }

    private void ShowToast(StreamToastModel toast)
    {
        _streamToastText.Text = toast.Text;
        _streamToast.IsVisible = true;
        _toastHideTimer ??= new DispatcherTimer
        {
            Interval = PlaybackChromePresenter.StreamToastAutoHide
        };
        _toastHideTimer.Tick -= OnToastHideTick;
        _toastHideTimer.Tick += OnToastHideTick;
        _toastHideTimer.Stop();
        _toastHideTimer.Start();
    }

    private void OnToastHideTick(object? sender, EventArgs e)
    {
        _toastHideTimer?.Stop();
        _chrome.DismissStreamToast();
    }

    private void HideToast()
    {
        _toastHideTimer?.Stop();
        _streamToast.IsVisible = false;
    }

    private static AvButton MakeChromeButton(string content, double width, double height)
    {
        return new AvButton
        {
            Content = content,
            Width = width,
            Height = height,
            Padding = new AvThickness(0),
            Background = new AvSolidColorBrush(AvColor.FromArgb(0xCC, 0x1A, 0x1A, 0x1A)),
            Foreground = AvBrushes.White,
            CornerRadius = new AvCornerRadius(4),
            HorizontalContentAlignment = AvHorizontalAlignment.Center,
            VerticalContentAlignment = AvVerticalAlignment.Center
        };
    }

    private static AvButton MakeMenuItem(string label, Action onClick)
    {
        var button = new AvButton
        {
            Content = label,
            HorizontalAlignment = AvHorizontalAlignment.Stretch,
            HorizontalContentAlignment = AvHorizontalAlignment.Left,
            Background = AvBrushes.Transparent,
            Foreground = AvBrushes.White,
            BorderThickness = new AvThickness(0),
            Padding = new AvThickness(12, 8, 12, 8),
            FontSize = 14,
            CornerRadius = new AvCornerRadius(4)
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
