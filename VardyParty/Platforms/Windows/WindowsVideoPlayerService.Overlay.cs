using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Playback;
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
                        // Live HLS leaves BufferingProgress at 0 while playing — use ranges.
                        BufferPercent = ReadPlaybackBufferPercent(session),
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

                var indexText = PlayerChromeText.FormatStreamIdentityIndex(model);
                infoStreamIndexText.Text = indexText;
                infoStreamIndexText.Visibility = string.IsNullOrEmpty(indexText)
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

                var suffix = PlayerChromeText.FormatStreamIdentitySuffix(model, whileSwitching);
                infoIdentitySuffixText.Text = suffix;
                infoIdentitySuffixText.Visibility = string.IsNullOrEmpty(suffix)
                    ? Microsoft.UI.Xaml.Visibility.Collapsed
                    : Microsoft.UI.Xaml.Visibility.Visible;

                var identityVisible = infoStreamIndexText.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                    || infoSourceBadge.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                    || infoIdentitySuffixText.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
                infoIdentityRow.Visibility = identityVisible
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;

                infoQualityText.Text = PlayerChromeText.FormatQualityLine(model, whileSwitching);
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
                        chrome.SetContentState(PlaybackChromeContentState.Error);
                        infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                        infoGameTitleText.Text = string.Empty;
                        infoStatusText.Text = message;
                        infoStreamIndexText.Text = string.Empty;
                        infoStreamIndexText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        infoSourceBadge.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        infoIdentitySuffixText.Text = string.Empty;
                        infoIdentitySuffixText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        infoIdentityRow.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        infoQualityText.Text = string.Empty;
                        infoQualityText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        infoDetailText.Text = string.Empty;
                        infoDetailText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                        ApplyNextButtonChrome();
                        ApplyStreamToastHoldVisuals();
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
                // UpdateStreamInfo used to no-op while video-info was open; refresh Next now.
                UpdateStreamInfo();
            }

            /// <summary>
            /// Buffer fill for the video-info panel. <see cref="MediaPlaybackSession.BufferingProgress"/>
            /// is only useful while the session is actively buffering; for live HLS it stays 0
            /// once Playing. Prefer buffered/seekable time ahead of <see cref="MediaPlaybackSession.Position"/>
            /// relative to <see cref="PlaybackPolicy.DesiredLiveOffsetSeconds"/> (same target as AMS).
            /// </summary>
            private static int? ReadPlaybackBufferPercent(MediaPlaybackSession session)
            {
                try
                {
                    if (session.PlaybackState == MediaPlaybackState.Buffering
                        && session.BufferingProgress > 0)
                    {
                        return (int)Math.Clamp(Math.Round(session.BufferingProgress * 100), 0, 100);
                    }

                    var position = session.Position;
                    var targetSeconds = (double)PlaybackPolicy.DesiredLiveOffsetSeconds;
                    if (targetSeconds <= 0)
                        targetSeconds = 25;

                    var aheadSeconds = TryBufferedAheadSeconds(session, position)
                        ?? TrySeekableAheadSeconds(session, position);
                    if (aheadSeconds is double ahead && ahead >= 0)
                        return (int)Math.Clamp(Math.Round(100.0 * ahead / targetSeconds), 0, 100);

                    if (session.BufferingProgress > 0)
                        return (int)Math.Clamp(Math.Round(session.BufferingProgress * 100), 0, 100);

                    // Keep the Buffer line stable (Android always shows BufferedPercentage,
                    // including 0%). Returning null drops the row and shifts detail up.
                    return 0;
                }
                catch
                {
                    return 0;
                }
            }

            private static double? TryBufferedAheadSeconds(MediaPlaybackSession session, TimeSpan position)
            {
                try
                {
                    var ranges = session.GetBufferedRanges();
                    if (ranges == null || ranges.Count == 0)
                        return null;

                    TimeSpan? end = null;
                    foreach (var range in ranges)
                    {
                        if (range.End <= position)
                            continue;
                        // Range covering the playhead, or the nearest ahead chunk.
                        if (range.Start <= position)
                            end = range.End;
                        else if (end is null || range.End > end)
                            end = range.End;
                    }

                    return end is { } bufferedEnd
                        ? Math.Max(0, (bufferedEnd - position).TotalSeconds)
                        : null;
                }
                catch
                {
                    return null;
                }
            }

            private static double? TrySeekableAheadSeconds(MediaPlaybackSession session, TimeSpan position)
            {
                try
                {
                    // Live: seekable end is the available edge; distance from Position ≈ buffer depth.
                    var ranges = session.GetSeekableRanges();
                    if (ranges == null || ranges.Count == 0)
                        return null;

                    var liveEdge = ranges[ranges.Count - 1].End;
                    if (liveEdge <= position)
                        return 0;

                    return (liveEdge - position).TotalSeconds;
                }
                catch
                {
                    return null;
                }
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
                else if (infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible
                    && chrome.ContentState != PlaybackChromeContentState.Error)
                {
                    // Error feedback uses infoPanel without chrome.IsVideoInfoVisible.
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

                ApplyNextButtonChrome();
                ApplyStreamToastHoldVisuals();
                SyncScoresTickerFromChrome();
                RefreshDismissSurface();
            }

            private void SyncChromeContentStateFromPlayback()
            {
                var previous = chrome.ContentState;
                PlaybackChromeContentState proposed;
                if (streamSwitchInProgress)
                {
                    proposed = PlaybackChromeContentState.Switching;
                }
                else if (liveHlsSoftRecoverInFlight
                    || session.Snapshot.IsRecovering
                    || session.Snapshot.IsRecoveringSwitch)
                {
                    proposed = PlaybackChromeContentState.Recovering;
                }
                else
                {
                    MediaPlaybackState playbackState;
                    try { playbackState = mediaPlayer.PlaybackSession.PlaybackState; }
                    catch { playbackState = MediaPlaybackState.None; }

                    proposed = playbackState switch
                    {
                        MediaPlaybackState.Opening => PlaybackChromeContentState.Opening,
                        MediaPlaybackState.None => PlaybackChromeContentState.Opening,
                        _ => PlaybackChromeContentState.Playing
                    };
                }

                var next = PlaybackChromePresenter.ResolveFromPlayback(
                    previous,
                    proposed,
                    session.Snapshot.HasEstablishedPlayback);
                chrome.SetContentState(next);

                // Error uses infoPanel without chrome video-info; clear only when
                // leaving Error deliberately (ResolveFromPlayback cleared it).
                if (previous == PlaybackChromeContentState.Error
                    && chrome.ContentState != PlaybackChromeContentState.Error
                    && !chrome.IsVideoInfoVisible
                    && infoPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
                {
                    infoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    RefreshDismissSurface();
                }

                ApplyNextButtonChrome();
                ApplyStreamToastHoldVisuals();
            }

            /// <summary>
            /// Next sits at z70 while playing (hover-near fade). While awaiting a playable
            /// stream it must stay opaque and above video-info (z100) so it stays tappable.
            /// </summary>
            private void ApplyNextButtonChrome()
            {
                if (nextButtonHotZone is null || nextButtonContainer is null) return;

                var canSwitchToAnother = chrome.CanGoNext && _onNextStreamRequested != null;
                nextButtonHotZone.Visibility = canSwitchToAnother
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;

                var forced = chrome.IsNextForcedVisible;
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(nextButtonHotZone, forced ? 120 : 70);

                if (!canSwitchToAnother)
                {
                    isPointerNearNextButton = false;
                    nextButtonContainer.Opacity = 0;
                    nextButtonHintText.Text = string.Empty;
                    nextButtonHintText.Opacity = 0;
                    return;
                }

                if (chrome.StreamToast is { } toast)
                    nextButtonHintText.Text = $"{toast.Index}/{toast.Total}";

                nextButtonContainer.Opacity = forced || isPointerNearNextButton ? 1 : 0;
                nextButtonHintText.Opacity = forced || isPointerNearNextButton ? 1 : 0;
            }

            private void ApplyStreamToastHoldVisuals()
            {
                if (streamInfoPanel is null) return;
                if (chrome.IsVideoInfoVisible)
                {
                    streamInfoHideTimer?.Stop();
                    streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    return;
                }

                if (!chrome.IsStreamToastVisible)
                {
                    if (!chrome.ShouldHoldStreamToast)
                        streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    return;
                }

                streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                if (chrome.ShouldHoldStreamToast)
                {
                    streamInfoHideTimer?.Stop();
                    return;
                }

                EnsureStreamInfoHideTimer();
                streamInfoHideTimer!.Stop();
                streamInfoHideTimer.Start();
            }

            private void EnsureStreamInfoHideTimer()
            {
                streamInfoHideTimer ??= streamInfoPanel.DispatcherQueue.CreateTimer();
                streamInfoHideTimer.Interval = PlaybackChromePresenter.StreamToastAutoHide;
                if (streamInfoHideHandler != null) return;

                streamInfoHideHandler = (_, __) =>
                {
                    streamInfoHideTimer?.Stop();
                    chrome.DismissStreamToast();
                    if (!chrome.IsStreamToastVisible)
                        streamInfoPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                };
                streamInfoHideTimer.Tick += streamInfoHideHandler;
            }

            private void ShowNextButtonChrome()
            {
                if (nextButtonHotZone.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;
                nextButtonContainer.Opacity = 1;
                if (!string.IsNullOrWhiteSpace(nextButtonHintText.Text))
                    nextButtonHintText.Opacity = 1;
            }

            private void HideNextButtonChrome()
            {
                // Hover-near fade is only for actively playing content.
                if (chrome.IsNextForcedVisible)
                {
                    nextButton.Background = nextBgNormal;
                    nextButtonContainer.Opacity = 1;
                    if (!string.IsNullOrWhiteSpace(nextButtonHintText.Text))
                        nextButtonHintText.Opacity = 1;
                    return;
                }

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
                    var sourceLabel = current?.Stream?.CatalogSourceBadgeLabel;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        ApplyNextButtonChrome();

                        // Toast / badge stay hidden under the video-info panel.
                        if (chrome.IsVideoInfoVisible)
                            return;

                        streamCountText.Text = chrome.StreamToast?.Text
                            ?? PlayerOverlayFormatter.FormatStreamToast(index, total, null);

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

                        // Index/total change or durable hold while awaiting playable content.
                        if (total > 0 && (hasChanged || chrome.ShouldHoldStreamToast || chrome.IsStreamToastVisible))
                            ApplyStreamToastHoldVisuals();
                    });
                }
                catch (Exception ex) { _host.LogIgnored("UpdateStreamInfo", ex); }
            }
        }
    }
}
