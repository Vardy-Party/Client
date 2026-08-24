using Microsoft.Extensions.Logging;
using VardyParty.Services;
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
                        int gcd(int a, int b) => b == 0 ? a : gcd(b, a % b);
                        int g = gcd((int)width, (int)height);
                        sb.AppendLine($"Aspect ratio: {(int)width / g}:{(int)height / g} ({r:0.00})");
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

                    string StripQuery(string url)
                    {
                        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
                        try
                        {
                            var uri = new Uri(url);
                            var builder = new UriBuilder(uri) { Query = string.Empty };
                            return builder.Uri.ToString();
                        }
                        catch
                        {
                            var idx = url.IndexOf('?', StringComparison.Ordinal);
                            return idx >= 0 ? url.Substring(0, idx) : url;
                        }
                    }

                    string refHost = _refererUrl;
                    if (Uri.TryCreate(_refererUrl, UriKind.Absolute, out var rUri)) refHost = rUri.Host;

                    var source = StripQuery(currentPlaybackUrl);
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

                    string? ExtractVerticalResolution(string? resolution)
                    {
                        if (string.IsNullOrWhiteSpace(resolution)) return null;
                        var match = System.Text.RegularExpressions.Regex.Match(resolution, @"(\d{3,4})\s*[xX]\s*(\d{3,4})");
                        if (match.Success)
                        {
                            return $"{match.Groups[2].Value}p";
                        }
                        return null;
                    }

                    var verticalResolution =
                        ExtractVerticalResolution(current?.Health?.Resolution)
                        ?? ExtractVerticalResolution(current?.Stream?.Resolution)
                        ?? (_host._currentMetrics?.Resolution is { } r ? $"{r.Item2}p" : null);

                    var hasChanged = total != lastStreamTotal || index != lastStreamIndex;
                    var hasResolutionChanged = !string.Equals(lastStreamVerticalResolution ?? string.Empty, verticalResolution ?? string.Empty, StringComparison.Ordinal);
                    lastStreamTotal = total;
                    lastStreamIndex = index;
                    lastStreamVerticalResolution = verticalResolution;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                        {
                            return;
                        }

                        if (total > 0)
                        {
                            streamCountText.Text = string.IsNullOrWhiteSpace(verticalResolution)
                                ? $"Stream: {index}/{total}"
                                : $"Stream: {index}/{total} ({verticalResolution})";
                        }
                        else
                        {
                            streamCountText.Text = "Streams: 0";
                        }

                        var sourceLabel = current?.Stream?.CatalogSourceBadgeLabel;
                        if (!string.IsNullOrWhiteSpace(sourceLabel))
                        {
                            streamSourceBadgeText.Text = sourceLabel;
                            if (string.Equals(sourceLabel, "FB", StringComparison.OrdinalIgnoreCase))
                            {
                                streamSourceBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                    global::Windows.UI.Color.FromArgb(0xFF, 0x1E, 0x3A, 0x5F));
                                streamSourceBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                    global::Windows.UI.Color.FromArgb(0xFF, 0x93, 0xC5, 0xFD));
                            }
                            else
                            {
                                streamSourceBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                    global::Windows.UI.Color.FromArgb(0xFF, 0x3B, 0x07, 0x64));
                                streamSourceBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                    global::Windows.UI.Color.FromArgb(0xFF, 0xD8, 0xB4, 0xFE));
                            }
                            streamSourceBadge.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                        }
                        else
                        {
                            streamSourceBadge.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        }

                        var canSwitchToAnother = _onNextStreamRequested != null && total > 1;
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