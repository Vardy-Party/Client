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

                    // Get additional info from MediaPlaybackItem if available
                    string frameRateText = "unknown";
                    string vCodec = "unknown";
                    string aCodec = "unknown";
                    AdaptiveMediaSource? ams = null;

                    if (mediaPlayer.Source is MediaPlaybackItem item)
                    {
                        // Extract and store video metadata for health reporting
                        _host.ExtractVideoMetadata(item, mediaPlayer);
                    }
                    else if (mediaPlayer.Source is MediaSource ms)
                    {
                        ams = ms.AdaptiveMediaSource;
                    }

                    var sb = new System.Text.StringBuilder();

                    int streamIndex = 0;
                    int streamTotal = 0;
                    string? streamChannel = null;
                    string? streamQuality = null;
                    string? streamSourceLabel = null;
                    try
                    {
                        var switching = switchingService;
                        if (switching != null)
                        {
                            streamIndex = switching.GetCurrentStreamIndex();
                            streamTotal = switching.GetHealthyStreams().Count;
                            var current = switching.GetCurrentStream();
                            streamChannel = current?.Stream?.Channel;
                            try { streamQuality = current?.GetQualityDisplay(); } catch (Exception ex) { _host.LogIgnored("GetQualityDisplay", ex); }
                            try { streamSourceLabel = current?.Stream?.CatalogSourceBadgeLabel; } catch (Exception ex) { _host.LogIgnored("CatalogSourceBadgeLabel", ex); }
                        }
                    }
                    catch (Exception ex) { _host.LogIgnored("ReadCurrentStreamOverlay", ex); }

                    sb.AppendLine($"Status: {state}");
                    if (streamTotal > 0)
                        sb.AppendLine($"Stream: {streamIndex}/{streamTotal}");
                    if (!string.IsNullOrEmpty(streamChannel))
                        sb.AppendLine($"Channel: {streamChannel}");
                    if (!string.IsNullOrEmpty(streamSourceLabel))
                        sb.AppendLine($"Source: {streamSourceLabel}");
                    if (!string.IsNullOrEmpty(streamQuality))
                        sb.AppendLine($"Quality: {streamQuality}");
                    sb.AppendLine($"Resolution: {width}x{height} @ {frameRateText}");

                    if (width > 0 && height > 0)
                    {
                        double r = (double)width / height;
                        var aspect = PlayerOverlayFormatter.BuildAspect(width, height) ?? "pending";
                        sb.AppendLine($"Aspect ratio: {aspect} ({r:0.00})");
                    }
                    else
                    {
                        sb.AppendLine($"Aspect ratio: pending");
                    }

                    string bitrateText = "unknown";
                    if (ams != null && ams.CurrentDownloadBitrate > 0)
                        bitrateText = $"{ams.CurrentDownloadBitrate / 1024.0:0.0} kbps";

                    sb.AppendLine($"Bitrate: {bitrateText}");
                    sb.AppendLine($"Video Codec: {vCodec}");
                    sb.AppendLine($"Audio Codec: {aCodec}");


                    if (!string.IsNullOrEmpty(_title))
                        sb.AppendLine($"{_title}");

                    double bufferingProgress = 0;
                    try
                    {
                        bufferingProgress = session.BufferingProgress;
                    }
                    catch (Exception ex) { _host.LogIgnored("ReadBufferingProgress", ex); }
                    sb.AppendLine($"Buffer: {bufferingProgress * 100:0}%");

                    string refHost = PlayerOverlayFormatter.RefererHost(_refererUrl);

                    var source = PlayerOverlayFormatter.StripQuery(currentPlaybackUrl);
                    if (!string.IsNullOrEmpty(source))
                        sb.AppendLine($"Source: {source}");
                    if (!string.IsNullOrEmpty(refHost))
                        sb.AppendLine($"Referer: {refHost}");

                    infoText.Text = sb.ToString();
                });
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
                        infoText.Text = message;
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

                    var verticalResolution =
                        PlayerOverlayFormatter.ExtractVerticalResolutionLabel(current?.Health?.Resolution)
                        ?? PlayerOverlayFormatter.ExtractVerticalResolutionLabel(current?.Stream?.Resolution)
                        ?? (_host._currentMetrics?.Resolution is { } r ? $"{r.Item2}p" : null);

                    var overlay = new PlayerOverlayInfo
                    {
                        Index = index,
                        Total = total,
                        Channel = current?.Stream?.Channel,
                        Resolution = current?.Health?.Resolution ?? current?.Stream?.Resolution ?? verticalResolution,
                        Title = current?.Stream?.Channel
                    };
                    chrome.ApplyOverlayInfo(overlay);
                    chrome.NotifyHealthyCount(total);

                    var hasChanged = total != lastStreamTotal || index != lastStreamIndex;
                    var hasResolutionChanged = !string.Equals(lastStreamVerticalResolution ?? string.Empty, verticalResolution ?? string.Empty, StringComparison.Ordinal);
                    lastStreamTotal = total;
                    lastStreamIndex = index;
                    lastStreamVerticalResolution = verticalResolution;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (chrome.IsVideoInfoVisible)
                        {
                            return;
                        }

                        streamCountText.Text = chrome.StreamToast?.Text
                            ?? PlayerOverlayFormatter.FormatStreamToast(index, total, verticalResolution);

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

                        var shouldShowStreamOverlay = total > 0 &&
                            (hasChanged
                             || hasResolutionChanged
                             || (streamInfoPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible && !string.IsNullOrWhiteSpace(verticalResolution)));
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