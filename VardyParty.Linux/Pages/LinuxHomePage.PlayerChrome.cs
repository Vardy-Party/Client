using Avalonia.Input;
using Microsoft.Extensions.Logging;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Linux.Services;
using VardyParty.Presentation;

namespace VardyParty.Linux.Pages;

public partial class LinuxHomePage
{
    private IDispatcherTimer? _streamToastHideTimer;
    private IDispatcherTimer? _tickerTimer;
    private IDispatcherTimer? _reportStatusTimer;
    private double _tickerOffset;
    private bool _isNextStreamRequestInProgress;
    private Dictionary<string, List<Game>>? _latestGamesByLeague;
    private bool _playerChromeWired;
    private bool _isPlaybackBuffering;

    private void WirePlayerChrome()
    {
        if (_playerChromeWired)
        {
            return;
        }

        _playerChromeWired = true;
        EnsurePlaybackChrome();
        _chromeSubscriptions.Add(_switching.HealthyStreamsUpdated.Subscribe(_ =>
            Dispatcher.Dispatch(RefreshPlayerChromeFromSwitching)));
        _chromeSubscriptions.Add(_switching.CurrentStreamIndexChanged.Subscribe(_ =>
            Dispatcher.Dispatch(RefreshPlayerChromeFromSwitching)));
        _chromeSubscriptions.Add(_switching.OverlayInfoChanged.Subscribe(_ =>
            Dispatcher.Dispatch(OnOverlayInfoChanged)));
        _chromeSubscriptions.Add(_gameService.GamesStream.Subscribe(dict =>
            RememberGamesSnapshot(dict)));
        _videoPlayer.BufferingStateChanged += OnPlayerBufferingStateChanged;

        var hover = new PointerGestureRecognizer();
        hover.PointerEntered += (_, _) => ApplyCloseChip(_closeChip.OnHoverEnter());
        hover.PointerExited += (_, _) => ApplyCloseChip(_closeChip.OnHoverLeave());
        PlayerMenuButton.GestureRecognizers.Add(hover);
        NextStreamButton.GestureRecognizers.Add(hover);
        PreviousStreamButton.GestureRecognizers.Add(hover);
        FullscreenMenuButton.GestureRecognizers.Add(hover);
    }

    private void ResetPlayerChrome()
    {
        _streamToastHideTimer?.Stop();
        _tickerTimer?.Stop();
        _reportStatusTimer?.Stop();
        _tickerOffset = 0;
        _isPlaybackBuffering = false;
        _playbackChrome?.DismissStreamToast();
        _playbackChrome?.ClearReportStatus();
        if (_playbackChrome is { IsMenuVisible: true })
            _playbackChrome.HideMenu();
        if (_playbackChrome is { IsVideoInfoVisible: true })
            _playbackChrome.HideVideoInfo();
        if (_playbackChrome is { IsScoresVisible: true })
            _playbackChrome.TryDismissLayer();
        ApplyPlayerChromeVisuals();
    }

    private void RefreshPlayerChromeFromSwitching()
    {
        try
        {
            PushOverlayInfoFromSwitching();
            ApplyPlayerChromeVisuals();
            if (_playbackChrome?.IsVideoInfoVisible == true)
            {
                RefreshVideoInfoText();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxHome] RefreshPlayerChromeFromSwitching failed");
        }
    }

    private void OnOverlayInfoChanged()
    {
        RefreshPlayerChromeFromSwitching();
    }

    private void OnPlayerBufferingStateChanged(object? sender, bool isBuffering)
    {
        Dispatcher.Dispatch(() =>
        {
            _isPlaybackBuffering = isBuffering;
            ApplyPlayerChromeVisuals();
            if (_playbackChrome?.IsVideoInfoVisible == true)
            {
                RefreshVideoInfoText();
            }
        });
    }

    private void ApplyStreamToastTimer(bool start)
    {
        if (start)
        {
            _streamToastHideTimer ??= CreateStreamToastHideTimer();
            _streamToastHideTimer.Stop();
            _streamToastHideTimer.Start();
        }
        else
        {
            _streamToastHideTimer?.Stop();
        }
    }

    private IDispatcherTimer CreateStreamToastHideTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = PlaybackChromePresenter.StreamToastAutoHide;
        timer.Tick += (_, _) =>
        {
            _streamToastHideTimer?.Stop();
            _playbackChrome?.DismissStreamToast();
            ApplyPlayerChromeVisuals();
        };
        return timer;
    }

    private void ApplyPlayerChromeVisuals()
    {
        ApplyCloseChipVisuals();

        var chrome = _playbackChrome;
        var toast = chrome?.StreamToast;
        var toastVisible = chrome?.IsStreamToastVisible == true;
        var menuVisible = chrome?.IsMenuVisible == true;
        var infoVisible = chrome?.IsVideoInfoVisible == true;
        var scoresVisible = chrome?.IsScoresVisible == true;
        var reportVisible = chrome is not null
            && chrome.ReportState != PlaybackReportUiState.Idle
            && !string.IsNullOrEmpty(chrome.ReportStatusText);

        var needsExpanded = toastVisible || menuVisible || reportVisible;
        var chromeRevealed = _closeChip.ChipVisible || needsExpanded;

        StreamToastPanel.IsVisible = toastVisible;
        StreamToastLabel.Text = toast?.Text ?? string.Empty;

        var badge = _switching.GetCurrentStream()?.Stream?.CatalogSourceBadgeLabel;
        StreamSourceBadge.IsVisible = !string.IsNullOrWhiteSpace(badge) && toastVisible;
        if (!string.IsNullOrWhiteSpace(badge))
        {
            StreamSourceBadgeLabel.Text = badge;
            if (PlayerChromeText.IsFacebookSource(badge))
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

        PlaybackBufferingIndicator.IsVisible = _isPlaybackBuffering;
        PlaybackBufferingIndicator.IsRunning = _isPlaybackBuffering;

        var canSwitch = chromeRevealed && chrome?.CanGoNext == true;
        PreviousStreamButton.IsVisible = canSwitch;
        NextStreamCluster.IsVisible = canSwitch;
        NextStreamHintLabel.Text = toast is null
            ? string.Empty
            : PlayerChromeText.FormatStreamHint(toast.Index, toast.Total);

        PlayerMenuButton.Opacity = chromeRevealed || menuVisible ? 1 : 0;
        PlayerMenuButton.InputTransparent = !(chromeRevealed || menuVisible);
        PlayerMenuButton.IsEnabled = chromeRevealed || menuVisible;
        PlayerMenuPanel.IsVisible = menuVisible;
        ReportStreamStatusLabel.Text = chrome?.ReportStatusText ?? string.Empty;
        ReportStreamStatusLabel.IsVisible = reportVisible;

        if (_fullscreenSession.IsFullscreen)
            FullscreenMenuButton.Text = "Exit fullscreen";
        else
            FullscreenMenuButton.Text = "Fullscreen";

        VideoInfoPanel.IsVisible = infoVisible;
        ScoresTickerRow.IsVisible = scoresVisible;
        if (scoresVisible)
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
            var linux = _videoPlayer as LinuxVideoPlayerService;
            var model = PlayerChromeText.FromPlayback(
                BuildOverlaySnapshot(),
                _switching.GetCurrentStream(),
                playbackState: _isPlaybackBuffering ? "Buffering" : "Playing",
                title: linux?.PlaybackTitle,
                sourceUrl: _switching.GetCurrentStream()?.ResolvedM3U8Url,
                refererUrl: _switching.GetCurrentStream()?.Referer,
                bufferPercent: null);
            VideoInfoText.Text = PlayerChromeText.FormatVideoInfo(model);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[LinuxHome] RefreshVideoInfoText failed");
        }
    }

    private PlayerOverlayInfo? BuildOverlaySnapshot()
    {
        var current = _switching.GetCurrentStream();
        if (current == null)
        {
            return null;
        }

        return PlayerOverlayFormatter.BuildOverlayInfo(
            current,
            _switching.GetCurrentStreamIndex(),
            _switching.GetHealthyStreams().Count,
            current.Referer);
    }

    private void RefreshScoresTickerText()
    {
        if (_playbackChrome is null)
            return;

        var games = EnumerateLatestGames();
        var watched = _selection.CurrentGame ?? _homeShell.SelectedGame;
        var linux = _videoPlayer as LinuxVideoPlayerService;
        var snapshot = ScoresTickerText.Build(
            _playbackChrome.ScoresMode,
            games,
            watched?.DisplayLeague ?? linux?.PlaybackLeague,
            watched?.DisplayHome ?? linux?.PlaybackHomeTeam,
            watched?.DisplayAway ?? linux?.PlaybackAwayTeam);
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

        if (_gamesSnapshot.Count > 0)
        {
            return _gamesSnapshot;
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
        _gamesSnapshot = FlattenGames(dict);
        if (_playbackChrome?.IsScoresVisible == true)
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
        if (_playbackChrome?.IsScoresVisible != true)
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

    private void HandlePlaybackChromeKey(KeyEventArgs e)
    {
        if (_playbackChrome is null)
            return;

        switch (e.Key)
        {
            case Key.I:
                _playbackChrome.ToggleVideoInfo();
                if (_playbackChrome.IsVideoInfoVisible)
                    RefreshVideoInfoText();
                ApplyPlayerChromeVisuals();
                e.Handled = true;
                return;

            case Key.M:
                _playbackChrome.ToggleMenu();
                ApplyPlayerChromeVisuals();
                e.Handled = true;
                return;

            case Key.T:
            case Key.S:
                _playbackChrome.ToggleScores();
                ApplyPlayerChromeVisuals();
                e.Handled = true;
                return;

            case Key.R:
                _playbackChrome.CycleScoresMode();
                ApplyPlayerChromeVisuals();
                e.Handled = true;
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
        _playbackChrome?.ToggleMenu();
        ApplyPlayerChromeVisuals();
    }

    private void OnVideoInfoMenuClicked(object? sender, EventArgs e)
    {
        _playbackChrome?.ShowVideoInfo();
        RefreshVideoInfoText();
        ApplyPlayerChromeVisuals();
    }

    private void OnVideoInfoCloseClicked(object? sender, EventArgs e)
    {
        _playbackChrome?.HideVideoInfo();
        ApplyPlayerChromeVisuals();
    }

    private void OnScoresMenuClicked(object? sender, EventArgs e)
    {
        _playbackChrome?.ToggleScores();
        ApplyPlayerChromeVisuals();
    }

    private void OnTickerCycleClicked(object? sender, EventArgs e)
    {
        _playbackChrome?.CycleScoresMode();
        ApplyPlayerChromeVisuals();
    }

    private void OnFullscreenMenuClicked(object? sender, EventArgs e)
    {
        _playbackChrome?.HideMenu();
        ApplyPlayerChromeVisuals();
        TogglePlaybackFullscreen();
    }

    private async void OnReportStreamClicked(object? sender, EventArgs e)
    {
        if (_playbackChrome is null)
            return;

        await _playbackChrome.ReportBadStreamAsync();
        ApplyPlayerChromeVisuals();
        _reportStatusTimer ??= CreateReportStatusTimer();
        _reportStatusTimer.Stop();
        _reportStatusTimer.Start();
    }

    private IDispatcherTimer CreateReportStatusTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = PlaybackChromePresenter.ReportStatusLinger;
        timer.Tick += (_, _) =>
        {
            _reportStatusTimer?.Stop();
            _playbackChrome?.ClearReportStatus();
            _playbackChrome?.HideMenu();
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
        if (_videoPlayer is not LinuxVideoPlayerService linux || _isNextStreamRequestInProgress)
        {
            return;
        }

        if (_playbackChrome?.CanGoNext != true)
        {
            return;
        }

        _isNextStreamRequestInProgress = true;
        NextStreamButton.IsEnabled = false;
        try
        {
            await linux.RequestNextStreamAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxHome] Next stream failed");
        }
        finally
        {
            _isNextStreamRequestInProgress = false;
            NextStreamButton.IsEnabled = true;
        }
    }

    private void RequestPreviousStreamFromChrome()
    {
        if (_videoPlayer is not LinuxVideoPlayerService linux || _playbackChrome?.CanGoNext != true)
        {
            return;
        }

        try
        {
            linux.RequestPreviousStream();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LinuxHome] Previous stream failed");
        }
    }
}
