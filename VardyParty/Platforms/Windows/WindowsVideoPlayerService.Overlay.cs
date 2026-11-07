using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;

namespace VardyParty.Platforms.Windows
{
    public partial class WindowsVideoPlayerService
    {
        private sealed partial class PlayerSession
        {
            private void UpdateInfo()
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (infoPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;

                    var session = mediaPlayer.PlaybackSession;
                    var width = session.NaturalVideoWidth;
                    var height = session.NaturalVideoHeight;
                    var state = session.PlaybackState.ToString();

                    if (mediaPlayer.Source is MediaPlaybackItem item)
                        _host.ExtractVideoMetadata(item, mediaPlayer);

                    AdaptiveMediaSource? ams = null;
                    if (mediaPlayer.Source is MediaSource ms)
                        ams = ms.AdaptiveMediaSource;

                    EnrichedStream? current = null;
                    int streamIndex = 0;
                    int streamTotal = 0;
                    try
                    {
                        var switching = switchingService;
                        if (switching != null)
                        {
                            streamIndex = switching.GetCurrentStreamIndex();
                            streamTotal = switching.GetHealthyStreams().Count;
                            current = switching.GetCurrentStream();
                        }
                    }
                    catch (Exception ex) { _host.LogIgnored("ReadCurrentStreamOverlay", ex); }

                    double bufferingProgress = 0;
                    try { bufferingProgress = session.BufferingProgress; }
                    catch (Exception ex) { _host.LogIgnored("ReadBufferingProgress", ex); }

                    var bitrateKbps = _host.ResolveBitrateKbps(ams, transferMeter);

                    var metrics = _host.GetCurrentMetrics();
                    var overlay = new PlayerOverlayInfo
                    {
                        Index = streamIndex,
                        Total = streamTotal,
                        Channel = PlayerOverlayFormatter.ResolveChipOrChannelLabel(current?.Stream),
                        Resolution = width > 0 && height > 0
                            ? $"{width}x{height}"
                            : metrics?.Resolution is { } r ? $"{r.Width}x{r.Height}" : null,
                        BitrateKbps = bitrateKbps ?? metrics?.BitrateKbps,
                        FrameRate = metrics?.Framerate is int fps ? fps : null,
                        VideoCodec = metrics?.VideoCodec,
                        AudioCodec = metrics?.AudioCodec,
                        AspectRatio = width > 0 && height > 0
                            ? PlayerOverlayFormatter.BuildAspect(width, height)
                            : null,
                        BufferPercent = (int)(bufferingProgress * 100),
                        M3u8Url = currentPlaybackUrl,
                        RefererUrl = _refererUrl
                    };

                    var model = PlayerChromeText.FromPlayback(
                        overlay,
                        current,
                        playbackState: state,
                        title: _title,
                        sourceUrl: currentPlaybackUrl,
                        refererUrl: _refererUrl,
                        bufferPercent: overlay.BufferPercent);

                    ApplyVideoInfoPanel(model, streamSwitchInProgress, current?.Stream?.CatalogSourceBadgeLabel);
                });
            }

            private void ApplyVideoInfoPanel(VideoInfoPanelModel model, bool whileSwitching, string? sourceLabel)
            {
                infoGameTitleText.Text = model.Title ?? string.Empty;
                infoStatusText.Text = PlayerChromeText.FormatStatusLine(model, whileSwitching);
                infoStreamIndexText.Text = PlayerChromeText.FormatStreamIndexLine(model);
                infoStreamIndexText.Visibility = string.IsNullOrEmpty(infoStreamIndexText.Text)
                    ? Microsoft.UI.Xaml.Visibility.Collapsed
                    : Microsoft.UI.Xaml.Visibility.Visible;

                var badgeStyle = SourceBadgeStyle.ForLabel(sourceLabel);
                if (badgeStyle is { } style && !string.IsNullOrWhiteSpace(sourceLabel))
                {
                    infoSourceBadgeText.Text = sourceLabel;
                    infoSourceBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        global::Windows.UI.Color.FromArgb(style.BgA, style.BgR, style.BgG, style.BgB));
                    infoSourceBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        global::Windows.UI.Color.FromArgb(style.FgA, style.FgR, style.FgG, style.FgB));
                    infoSourceBadge.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }
                else
                {
                    infoSourceBadge.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }

                infoQualityText.Text = whileSwitching ? string.Empty : (model.Quality ?? string.Empty);
                infoQualityText.Visibility = string.IsNullOrEmpty(infoQualityText.Text)
                    ? Microsoft.UI.Xaml.Visibility.Collapsed
                    : Microsoft.UI.Xaml.Visibility.Visible;

                infoDetailText.Text = PlayerChromeText.FormatVideoInfoDetail(model, whileSwitching);
                infoDetailText.Visibility = string.IsNullOrEmpty(infoDetailText.Text)
                    ? Microsoft.UI.Xaml.Visibility.Collapsed
                    : Microsoft.UI.Xaml.Visibility.Visible;
            }

            private void RefreshDismissSurface()
            {
                dismissSurface.Visibility =
                    menuPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible ||
                    infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                        ? Microsoft.UI.Xaml.Visibility.Visible
                        : Microsoft.UI.Xaml.Visibility.Collapsed;
            }

            private void ShowStreamError(string message)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                        infoGameTitleText.Text = string.Empty;
                        infoStatusText.Text = message;
                        infoStreamIndexText.Text = string.Empty;
                        infoStreamIndexText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        infoSourceBadge.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        infoQualityText.Text = string.Empty;
                        infoQualityText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        infoDetailText.Text = string.Empty;
                        infoDetailText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    }
                    catch (Exception ex) { _host.LogIgnored("ShowStreamError", ex); }
                });
            }

            private void ShowPlayerOverlay()
            {
                var content = nativeWindow?.Content;
                if (ReferenceEquals(content, playerGrid))
                {
                    playerOverlayAttached = true;
                    return;
                }

                if (content is Microsoft.UI.Xaml.UIElement currentRoot
                    && !ReferenceEquals(currentRoot, playerGrid))
                {
                    originalContent = currentRoot;
                }

                if (nativeWindow is not null)
                    nativeWindow.Content = playerGrid;
                playerOverlayAttached = true;
                if (matchEventHandler == null) InitializeMatchToastOverlay();
                _host._logger.LogInformation("Player grid set as window content");
            }

            private void HidePlayerOverlay()
            {
                if (nativeWindow?.Content is Microsoft.UI.Xaml.UIElement current
                    && ReferenceEquals(current, playerGrid)
                    && originalContent is Microsoft.UI.Xaml.UIElement restored)
                {
                    nativeWindow.Content = restored;
                }

                TeardownMatchToastOverlay();
                playerOverlayAttached = false;
            }

            private void HideVideoInfoPanel()
            {
                infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                RefreshDismissSurface();
            }

            private void ShowVideoInfoPanel()
            {
                streamInfoHideTimer?.Stop();
                streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                UpdateInfo();
                RefreshDismissSurface();
                try { playerGrid.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { }
            }

            private void ApplyChromeState()
            {
                menuPanel.Visibility = chrome.IsMenuVisible
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;

                if (chrome.IsVideoInfoVisible)
                {
                    if (infoPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
                        ShowVideoInfoPanel();
                }
                else if (infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                {
                    HideVideoInfoPanel();
                }

                if (chrome.ReportState == PlaybackReportUiState.Idle)
                {
                    reportStatusText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                }
                else
                {
                    reportStatusText.Text = chrome.ReportStatusText;
                    reportStatusText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                }

                SyncScoresTickerFromChrome();
                RefreshDismissSurface();
            }

            private void ShowNextButtonChrome()
            {
                if (nextButtonHotZone.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;
                nextButtonContainer.Opacity = 1;
            }

            private void HideNextButtonChrome()
            {
                nextButtonContainer.Opacity = 0;
                nextButton.Background = nextBgNormal;
                nextButtonHintText.Opacity = 0;
            }

            private void UpdateStreamInfo()
            {
                if (switchingService == null) return;
                try
                {
                    var total = switchingService.GetHealthyStreams().Count;
                    var index = switchingService.GetCurrentStreamIndex();
                    var current = switchingService.GetCurrentStream();

                    // Video-info Resolution only — switch toast is index/total (no quality flash).
                    var resolutionForInfo = current?.Health?.Resolution
                        ?? current?.Stream?.Resolution
                        ?? (_host._currentMetrics?.Resolution is { } r ? $"{r.Item2}p" : null);

                    var overlay = new PlayerOverlayInfo
                    {
                        Index = index,
                        Total = total,
                        Channel = current?.Stream?.Channel,
                        Resolution = resolutionForInfo,
                        Title = current?.Stream?.Channel
                    };
                    chrome.ApplyOverlayInfo(overlay);
                    chrome.NotifyHealthyCount(total);

                    var hasChanged = total != lastStreamTotal || index != lastStreamIndex;
                    lastStreamTotal = total;
                    lastStreamIndex = index;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (chrome.IsVideoInfoVisible)
                        {
                            return;
                        }

                        streamCountText.Text = chrome.StreamToast?.Text
                            ?? PlayerOverlayFormatter.FormatStreamToast(index, total, null);

                        var sourceLabel = current?.Stream?.CatalogSourceBadgeLabel;
                        var badgeStyle = SourceBadgeStyle.ForLabel(sourceLabel);
                        if (badgeStyle is { } style && !string.IsNullOrWhiteSpace(sourceLabel))
                        {
                            streamSourceBadgeText.Text = sourceLabel;
                            streamSourceBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                global::Windows.UI.Color.FromArgb(style.BgA, style.BgR, style.BgG, style.BgB));
                            streamSourceBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                global::Windows.UI.Color.FromArgb(style.FgA, style.FgR, style.FgG, style.FgB));
                            streamSourceBadge.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                        }
                        else
                        {
                            streamSourceBadge.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        }

                        var canSwitchToAnother = chrome.CanGoNext && _onNextStreamRequested != null;
                        nextButtonHotZone.Visibility = canSwitchToAnother
                            ? Microsoft.UI.Xaml.Visibility.Visible
                            : Microsoft.UI.Xaml.Visibility.Collapsed;
                        if (!canSwitchToAnother)
                        {
                            isPointerNearNextButton = false;
                            nextButtonContainer.Opacity = 0;
                            nextButtonHintText.Text = string.Empty;
                            nextButtonHintText.Opacity = 0;
                        }
                        else
                        {
                            nextButtonHintText.Text = $"{index}/{total}";
                            nextButtonContainer.Opacity = isPointerNearNextButton ? 1 : 0;
                        }

                        // Index/total only — match presenter + Android; do not re-flash on measured size.
                        var shouldShowStreamOverlay = total > 0 && hasChanged;
                        if (shouldShowStreamOverlay)
                        {
                            streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                            streamInfoHideTimer ??= streamInfoPanel.DispatcherQueue.CreateTimer();
                            streamInfoHideTimer.Interval = TimeSpan.FromSeconds(10);
                            streamInfoHideTimer.Stop();
                            if (streamInfoHideHandler == null)
                            {
                                streamInfoHideHandler = (_, __) =>
                                {
                                    streamInfoHideTimer?.Stop();
                                    streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                                };
                                streamInfoHideTimer.Tick += streamInfoHideHandler;
                            }
                            streamInfoHideTimer.Start();
                        }
                    });
                }
                catch (Exception ex) { _host.LogIgnored("UpdateStreamInfo", ex); }
            }
        }
    }
}
