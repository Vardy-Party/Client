using Avalonia.Input;
using Microsoft.Extensions.Logging;
using VardyParty.Catalog;
using VardyParty.Desktop.Services;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Presentation;

namespace VardyParty.Desktop.Pages;

public partial class DesktopHomePage
{
    private readonly DesktopPlayerChrome _playerChrome = new();
    private readonly List<IDisposable> _playerChromeSubscriptions = new();
    private IDispatcherTimer? _streamToastHideTimer;
    private IDispatcherTimer? _tickerTimer;
    private IDispatcherTimer? _reportStatusTimer;
    private double _tickerOffset;
    private bool _isNextStreamRequestInProgress;
    private Dictionary<string, List<Game>>? _latestGamesByLeague;
    private bool _playerChromeWired;

    private void WirePlayerChrome()
    {
        if (_playerChromeWired)
        {
            return;
        }

        _playerChromeWired = true;
        _playerChromeSubscriptions.Add(_switching.HealthyStreamsUpdated.Subscribe(_ =>
            Dispatcher.Dispatch(RefreshPlayerChromeFromSwitching)));
        _playerChromeSubscriptions.Add(_switching.CurrentStreamIndexChanged.Subscribe(_ =>
            Dispatcher.Dispatch(RefreshPlayerChromeFromSwitching)));
        _playerChromeSubscriptions.Add(_switching.OverlayInfoChanged.Subscribe(_ =>
            Dispatcher.Dispatch(OnOverlayInfoChanged)));
        _videoPlayer.BufferingStateChanged += OnPlayerBufferingStateChanged;

        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => ApplyCloseChip(_closeChip.OnHoverEnter());
        hover.PointerExited += (_, _) => ApplyCloseChip(_closeChip.OnHoverLeave());
        PlayerMenuButton.GestureRecognizers.Add(hover);
        NextStreamButton.GestureRecognizers.Add(hover);
        PreviousStreamButton.GestureRecognizers.Add(hover);
    }

    private void ResetPlayerChrome()
    {
        _streamToastHideTimer?.Stop();
        _tickerTimer?.Stop();
        _reportStatusTimer?.Stop();
        _tickerOffset = 0;
        _playerChrome.Reset();
        ApplyPlayerChromeVisuals();
    }

    private void RefreshPlayerChromeFromSwitching()
    {
        try
        {
            var current = _switching.GetCurrentStream();
            var index = _switching.GetCurrentStreamIndex();
            var total = _switching.GetHealthyStreams().Count;
            var resolution = current?.Health?.Resolution ?? current?.Stream?.Resolution;
            var vertical = PlayerChromeText.ExtractVerticalResolution(resolution);
            var badge = current?.Stream?.CatalogSourceBadgeLabel;
            var canNext = _videoPlayer is DesktopVideoPlayerService;

            var action = _playerChrome.ApplyStreamChrome(index, total, vertical, badge, canNext);
            ApplyPlayerChromeVisuals();
            ApplyStreamToastTimer(action);
            if (_playerChrome.InfoVisible)
            {
                RefreshVideoInfoText();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopHome] RefreshPlayerChromeFromSwitching failed");
        }
    }

    private void OnOverlayInfoChanged()
    {
        RefreshPlayerChromeFromSwitching();
        if (_playerChrome.InfoVisible)
        {
            RefreshVideoInfoText();
        }
    }

    private void OnPlayerBufferingStateChanged(object? sender, bool isBuffering)
    {
        Dispatcher.Dispatch(() =>
        {
            _playerChrome.SetBuffering(isBuffering);
            ApplyPlayerChromeVisuals();
            if (_playerChrome.InfoVisible)
            {
                RefreshVideoInfoText();
            }
        });
    }

    private void ApplyStreamToastTimer(DesktopPlayerChromeTimerAction action)
    {
        switch (action)
        {
            case DesktopPlayerChromeTimerAction.StartStreamToastHide:
                _streamToastHideTimer ??= CreateStreamToastHideTimer();
                _streamToastHideTimer.Stop();
                _streamToastHideTimer.Start();
                break;
            case DesktopPlayerChromeTimerAction.CancelStreamToastHide:
                _streamToastHideTimer?.Stop();
                break;
        }
    }

    private IDispatcherTimer CreateStreamToastHideTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = DesktopPlayerChrome.StreamToastDuration;
        timer.Tick += (_, _) =>
        {
            _streamToastHideTimer?.Stop();
            _playerChrome.HideStreamToast();
            ApplyPlayerChromeVisuals();
        };
        return timer;
    }

    private void ApplyPlayerChromeVisuals()
    {
        ApplyCloseChipVisuals();

        var chromeRevealed = _closeChip.ChipVisible || _playerChrome.NeedsExpandedChromeRow;
        StreamToastPanel.IsVisible = _playerChrome.StreamToastVisible;
        StreamToastLabel.Text = _playerChrome.StreamToastText;

        var badge = _playerChrome.SourceBadgeLabel;
        StreamSourceBadge.IsVisible = !string.IsNullOrWhiteSpace(badge) && _playerChrome.StreamToastVisible;
        if (!string.IsNullOrWhiteSpace(badge))
        {
            StreamSourceBadgeLabel.Text = badge;
            if (_playerChrome.SourceBadgeIsFacebook)
            {
                StreamSourceBadge.BackgroundColor = Color.FromRgba(0x1E, 0x3A, 0x5F, 0xFF);
                StreamSourceBadgeLabel.TextColor = Color.FromRgba(0x93, 0xC5, 0xFD, 0xFF);
            }
            else
            {
                StreamSourceBadge.BackgroundColor = Color.FromRgba(0x3B, 0x07, 0x64, 0xFF);
                StreamSourceBadgeLabel.TextColor = Color.FromRgba(0xD8, 0xB4, 0xFE, 0xFF);
            }
        }

        PlaybackBufferingIndicator.IsVisible = _playerChrome.IsBuffering;
        PlaybackBufferingIndicator.IsRunning = _playerChrome.IsBuffering;

        var showSwitch = chromeRevealed && _playerChrome.CanSwitchStream;
        PreviousStreamButton.IsVisible = showSwitch;
        NextStreamCluster.IsVisible = showSwitch;
        NextStreamHintLabel.Text = _playerChrome.StreamCountHint;

        PlayerMenuButton.Opacity = chromeRevealed || _playerChrome.MenuVisible ? 1 : 0;
        PlayerMenuButton.InputTransparent = !(chromeRevealed || _playerChrome.MenuVisible);
        PlayerMenuButton.IsEnabled = chromeRevealed || _playerChrome.MenuVisible;
        PlayerMenuPanel.IsVisible = _playerChrome.MenuVisible;
        ReportStreamStatusLabel.Text = _playerChrome.ReportStatus;
        ReportStreamStatusLabel.IsVisible = _playerChrome.ReportStatusVisible;

        VideoInfoPanel.IsVisible = _playerChrome.InfoVisible;
        ScoresTickerRow.IsVisible = _playerChrome.TickerVisible;
        if (_playerChrome.TickerVisible)
        {
            RefreshScoresTickerText();
            StartTickerScroll();
        }
        else
        {
            _tickerTimer?.Stop();
        }
    }

    private void RefreshVideoInfoText()
    {
        try
        {
            var desktop = _videoPlayer as DesktopVideoPlayerService;
            var model = PlayerChromeText.FromPlayback(
                BuildOverlaySnapshot(),
                _switching.GetCurrentStream(),
                playbackState: _playerChrome.IsBuffering ? "Buffering" : "Playing",
                title: desktop?.PlaybackTitle,
                sourceUrl: _switching.GetCurrentStream()?.ResolvedM3U8Url,
                refererUrl: null,
                bufferPercent: null);
            VideoInfoText.Text = PlayerChromeText.FormatVideoInfo(model);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DesktopHome] RefreshVideoInfoText failed");
        }
    }

    private PlayerOverlayInfo? BuildOverlaySnapshot()
    {
        var current = _switching.GetCurrentStream();
        if (current == null)
        {
            return null;
        }

        return new PlayerOverlayInfo
        {
            Index = _switching.GetCurrentStreamIndex(),
            Total = _switching.GetHealthyStreams().Count,
            Channel = current.Stream?.Channel,
            BitrateKbps = current.Stream?.BitrateKbps ?? current.Health?.Bitrate,
            Resolution = current.Stream?.Resolution ?? current.Health?.Resolution,
            FrameRate = current.Health?.FrameRate,
            AspectRatio = null,
            Title = current.Stream?.Channel,
            M3u8Url = current.ResolvedM3U8Url
        };
    }

    private void RefreshScoresTickerText()
    {
        var games = EnumerateLatestGames();
        var watched = _selection.CurrentGame ?? _homeShell.SelectedGame;
        var desktop = _videoPlayer as DesktopVideoPlayerService;
        var snapshot = ScoresTickerText.Build(
            _playerChrome.TickerMode,
            games,
            watched?.DisplayLeague ?? desktop?.PlaybackLeague,
            watched?.DisplayHome ?? desktop?.PlaybackHomeTeam,
            watched?.DisplayAway ?? desktop?.PlaybackAwayTeam);
        TickerText1.Text = snapshot.FullText;
        TickerText2.Text = snapshot.FullText;
        _tickerOffset = 0;
        TickerTrack.TranslationX = 0;
    }

    private IEnumerable<Game> EnumerateLatestGames()
    {
        if (_latestGamesByLeague is { Count: > 0 })
        {
            return _latestGamesByLeague.Values.SelectMany(v => v);
        }

        var fromService = _gameService.GetLatestGames();
        if (fromService is { Count: > 0 })
        {
            return fromService.Values.SelectMany(v => v);
        }

        return _viewModel.Rows.SelectMany(row => row.Cards.Select(card => card.Game));
    }

    private void RememberGamesSnapshot(Dictionary<string, List<Game>>? dict)
    {
        if (dict == null)
        {
            return;
        }

        _latestGamesByLeague = dict.ToDictionary(
            pair => pair.Key,
            pair => pair.Value?.ToList() ?? new List<Game>());
        if (_playerChrome.TickerVisible)
        {
            Dispatcher.Dispatch(RefreshScoresTickerText);
        }
    }

    private void StartTickerScroll()
    {
        _tickerTimer ??= CreateTickerTimer();
        _tickerTimer.Stop();
        _tickerTimer.Start();
    }

    private IDispatcherTimer CreateTickerTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) => AdvanceTickerMarquee();
        return timer;
    }

    private void AdvanceTickerMarquee()
    {
        if (!_playerChrome.TickerVisible)
        {
            _tickerTimer?.Stop();
            return;
        }

        var contentWidth = Math.Max(TickerText1.Width, TickerText1.DesiredSize.Width);
        var viewportWidth = TickerViewport.Width;
        if (!TickerMarquee.ShouldLoop(contentWidth, viewportWidth))
        {
            TickerTrack.TranslationX = 0;
            TickerText2.IsVisible = false;
            TickerGap.IsVisible = false;
            return;
        }

        TickerText2.IsVisible = true;
        TickerGap.IsVisible = true;
        var period = TickerMarquee.LoopPeriod(contentWidth, 64);
        _tickerOffset = TickerMarquee.AdvanceLeft(_tickerOffset, 2, period);
        TickerTrack.TranslationX = _tickerOffset;
    }

    private void HandlePlaybackKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                switch (_playerChrome.OnEscape())
                {
                    case DesktopPlayerEscapeAction.DismissMenu:
                    case DesktopPlayerEscapeAction.DismissInfo:
                        ApplyPlayerChromeVisuals();
                        e.Handled = true;
                        return;
                    default:
                        OnClosePlaybackClicked(this, EventArgs.Empty);
                        e.Handled = true;
                        return;
                }

            case Key.I:
                _playerChrome.ToggleInfo();
                if (_playerChrome.InfoVisible)
                {
                    RefreshVideoInfoText();
                }

                ApplyPlayerChromeVisuals();
                e.Handled = true;
                return;

            case Key.M:
                _playerChrome.ToggleMenu();
                ApplyPlayerChromeVisuals();
                e.Handled = true;
                return;

            case Key.T:
            case Key.S:
                _playerChrome.ToggleTicker();
                ApplyPlayerChromeVisuals();
                e.Handled = true;
                return;

            case Key.R:
                if (_playerChrome.CycleTickerMode())
                {
                    ApplyPlayerChromeVisuals();
                    e.Handled = true;
                }

                return;

            case Key.N:
            case Key.Right:
                _ = RequestNextStreamFromChromeAsync();
                e.Handled = true;
                return;

            case Key.P:
            case Key.Left:
                RequestPreviousStreamFromChrome();
                e.Handled = true;
                return;
        }
    }

    private void OnPlayerMenuClicked(object? sender, EventArgs e)
    {
        ApplyCloseChip(_closeChip.OnTouched());
        _playerChrome.ToggleMenu();
        ApplyPlayerChromeVisuals();
    }

    private void OnVideoInfoMenuClicked(object? sender, EventArgs e)
    {
        _playerChrome.ShowInfo();
        RefreshVideoInfoText();
        ApplyPlayerChromeVisuals();
    }

    private void OnVideoInfoCloseClicked(object? sender, EventArgs e)
    {
        _playerChrome.HideInfo();
        ApplyPlayerChromeVisuals();
    }

    private void OnScoresMenuClicked(object? sender, EventArgs e)
    {
        _playerChrome.ToggleTicker();
        ApplyPlayerChromeVisuals();
    }

    private void OnTickerCycleClicked(object? sender, EventArgs e)
    {
        if (_playerChrome.CycleTickerMode())
        {
            ApplyPlayerChromeVisuals();
        }
    }

    private async void OnReportStreamClicked(object? sender, EventArgs e)
    {
        _playerChrome.SetReportStatus("Reporting stream...");
        ApplyPlayerChromeVisuals();
        try
        {
            await _orchestrator.ReportCurrentStreamAsBadAsync("User reported bad stream");
            _playerChrome.SetReportStatus("Stream reported");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopHome] Report stream failed");
            _playerChrome.SetReportStatus("Report failed");
        }

        ApplyPlayerChromeVisuals();
        _reportStatusTimer ??= CreateReportStatusTimer();
        _reportStatusTimer.Stop();
        _reportStatusTimer.Start();
    }

    private IDispatcherTimer CreateReportStatusTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(900);
        timer.Tick += (_, _) =>
        {
            _reportStatusTimer?.Stop();
            _playerChrome.SetReportStatus(null);
            _playerChrome.HideMenu();
            ApplyPlayerChromeVisuals();
        };
        return timer;
    }

    private async void OnNextStreamClicked(object? sender, EventArgs e) =>
        await RequestNextStreamFromChromeAsync();

    private void OnPreviousStreamClicked(object? sender, EventArgs e) =>
        RequestPreviousStreamFromChrome();

    private async Task RequestNextStreamFromChromeAsync()
    {
        if (_videoPlayer is not DesktopVideoPlayerService desktop || _isNextStreamRequestInProgress)
        {
            return;
        }

        if (!_playerChrome.CanSwitchStream)
        {
            return;
        }

        _isNextStreamRequestInProgress = true;
        NextStreamButton.IsEnabled = false;
        try
        {
            await desktop.RequestNextStreamAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopHome] Next stream failed");
        }
        finally
        {
            _isNextStreamRequestInProgress = false;
            NextStreamButton.IsEnabled = true;
        }
    }

    private void RequestPreviousStreamFromChrome()
    {
        if (_videoPlayer is not DesktopVideoPlayerService desktop || !_playerChrome.CanSwitchStream)
        {
            return;
        }

        try
        {
            desktop.RequestPreviousStream();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DesktopHome] Previous stream failed");
        }
    }
}
