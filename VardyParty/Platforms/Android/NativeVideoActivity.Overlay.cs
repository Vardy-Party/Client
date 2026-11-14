#if ANDROID
using System;
using System.Collections.Generic;
using Android.Widget;
using VardyParty.Catalog;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Presentation;

namespace VardyParty.Platforms.Android
{
    public partial class NativeVideoActivity
    {
        private void UpdateOverlayFromCurrentStream()
        {
            try
            {
                var info = BuildOverlayInfoFromCurrentStream();
                if (info == null)
                {
                    return;
                }

                UpdateOverlayText(info);
            }
            catch (Exception ex) { LogIgnored("UpdateOverlayFromCurrentStream", ex); }
        }

        /// <summary>
        /// Live Media3 <c>BufferedPercentage</c> collapses toward 0% even with a healthy
        /// live-edge backoff (same trap Windows hit with <c>BufferingProgress</c>). Report
        /// buffered-ahead vs <see cref="PlaybackPolicy.DesiredLiveOffsetSeconds"/> instead.
        /// </summary>
        private int? ReadPlaybackBufferPercent()
        {
            var player = _player;
            if (player is null)
                return null;

            try
            {
                var targetMs = (double)PlaybackPolicy.DesiredLiveOffsetSeconds * 1000.0;
                if (targetMs <= 0)
                    targetMs = 25_000;

                long aheadMs = player.TotalBufferedDuration;
                if (aheadMs < 0 || aheadMs == AndroidX.Media3.Common.C.TimeUnset)
                {
                    var buffered = player.BufferedPosition;
                    var current = player.CurrentPosition;
                    if (buffered >= 0 && current >= 0 && buffered != AndroidX.Media3.Common.C.TimeUnset
                        && current != AndroidX.Media3.Common.C.TimeUnset && buffered >= current)
                    {
                        aheadMs = buffered - current;
                    }
                    else
                    {
                        return player.BufferedPercentage;
                    }
                }

                return (int)Math.Clamp(Math.Round(100.0 * aheadMs / targetMs), 0, 100);
            }
            catch (Exception ex)
            {
                LogIgnored("ReadPlaybackBufferPercent", ex);
                try { return player.BufferedPercentage; }
                catch { return null; }
            }
        }

        private PlayerOverlayInfo? BuildOverlayInfoFromCurrentStream()
        {
            try
            {
                var current = _switching?.GetCurrentStream();
                if (current == null)
                {
                    return null;
                }

                var measuredResolution = _videoWidth is > 0 && _videoHeight is > 0
                    ? $"{_videoWidth}x{_videoHeight}"
                    : null;
                var resolution = measuredResolution
                    ?? current.Stream?.Resolution
                    ?? current.Health?.Resolution;
                // Match Windows: MeasuredBitrate so video-info and /health share one number.
                var bitrateKbps = MeasuredBitrate.ResolveKbps(
                    current.Stream?.BitrateKbps ?? current.Health?.Bitrate,
                    amsBitsPerSecond: 0,
                    _transferMeter.BitrateKbps);
                return new PlayerOverlayInfo
                {
                    Index = _switching?.GetCurrentStreamIndex() ?? 0,
                    Total = _switching?.GetHealthyStreams().Count ?? 0,
                    Channel = PlayerOverlayFormatter.ResolveChipOrChannelLabel(current.Stream),
                    BitrateKbps = bitrateKbps,
                    Resolution = resolution,
                    M3u8Url = current.ResolvedM3U8Url ?? _m3u8Url,
                    RefererUrl = _refererUrl,
                    BufferPercent = ReadPlaybackBufferPercent(),
                    FrameRate = current.Health?.FrameRate != null ? (double?)current.Health.FrameRate : null,
                    VideoCodec = PlayerOverlayFormatter.MapCodecToFriendlyName(current.Health?.VideoCodec),
                    AudioCodec = PlayerOverlayFormatter.MapCodecToFriendlyName(current.Health?.AudioCodec),
                    AspectRatio = PlayerOverlayFormatter.BuildAspect(
                        _videoWidth is > 0 && _videoHeight is > 0
                            ? $"{_videoWidth}x{_videoHeight}"
                            : resolution ?? current.Stream?.Resolution ?? current.Health?.Resolution),
                    Title = PlayerOverlayFormatter.ResolveChipOrChannelLabel(current.Stream)
                };
            }
            catch (Exception ex)
            {
                LogIgnored("BuildOverlayInfoFromCurrentStream", ex);
                return null;
            }
        }

        private void ApplySourceBadge(string? label)
        {
            if (_sourceBadgeView == null)
            {
                return;
            }

            var style = SourceBadgeStyle.ForLabel(label);
            if (style is null)
            {
                _sourceBadgeView.Visibility = global::Android.Views.ViewStates.Gone;
                return;
            }

            _sourceBadgeView.Text = label;
            _sourceBadgeView.SetBackgroundColor(global::Android.Graphics.Color.ParseColor(style.Value.BackgroundHex));
            _sourceBadgeView.SetTextColor(global::Android.Graphics.Color.ParseColor(style.Value.ForegroundHex));
            _sourceBadgeView.Visibility = global::Android.Views.ViewStates.Visible;
        }

        private void UpdateOverlayText(VardyParty.Kernel.PlayerOverlayInfo? info)
        {
            if (info == null)
            {
                if (_titleView != null) _titleView.Text = string.Empty;
                if (_statusView != null) _statusView.Text = string.Empty;
                if (_indexView != null) _indexView.Text = string.Empty;
                if (_identitySuffixView != null) _identitySuffixView.Text = string.Empty;
                if (_sourceBadgeView != null) _sourceBadgeView.Visibility = global::Android.Views.ViewStates.Gone;
                if (_identityRow != null) _identityRow.Visibility = global::Android.Views.ViewStates.Gone;
                if (_qualityView != null) _qualityView.Text = string.Empty;
                if (_resBrView != null) _resBrView.Text = string.Empty;
                return;
            }

            _lastOverlayInfo = info;

            var channelFallback = info.Channel ?? VardyParty.Resources.Strings.Resources.UnknownChannel;
            var current = _switching?.GetCurrentStream();
            var model = PlayerChromeText.FromPlayback(
                info,
                current,
                playbackState: _playbackStateText,
                title: BuildOverlayGameTitle(channelFallback),
                sourceUrl: info.M3u8Url,
                refererUrl: info.RefererUrl,
                bufferPercent: info.BufferPercent);

            if (_titleView != null) _titleView.Text = model.Title ?? string.Empty;
            if (_statusView != null) _statusView.Text = PlayerChromeText.FormatStatusLine(model, _streamSwitchInProgress);
            var indexText = PlayerChromeText.FormatStreamIdentityIndex(model);
            if (_indexView != null)
            {
                _indexView.Text = indexText;
                _indexView.Visibility = string.IsNullOrEmpty(indexText)
                    ? global::Android.Views.ViewStates.Gone
                    : global::Android.Views.ViewStates.Visible;
            }
            ApplySourceBadge(current?.Stream?.CatalogSourceBadgeLabel);
            var suffix = PlayerChromeText.FormatStreamIdentitySuffix(model, _streamSwitchInProgress);
            if (_identitySuffixView != null)
            {
                _identitySuffixView.Text = suffix;
                _identitySuffixView.Visibility = string.IsNullOrEmpty(suffix)
                    ? global::Android.Views.ViewStates.Gone
                    : global::Android.Views.ViewStates.Visible;
            }
            if (_identityRow != null)
            {
                var identityVisible = !string.IsNullOrEmpty(indexText)
                    || (_sourceBadgeView?.Visibility == global::Android.Views.ViewStates.Visible)
                    || !string.IsNullOrEmpty(suffix);
                _identityRow.Visibility = identityVisible
                    ? global::Android.Views.ViewStates.Visible
                    : global::Android.Views.ViewStates.Gone;
            }
            if (_qualityView != null)
                _qualityView.Text = PlayerChromeText.FormatQualityLine(model, _streamSwitchInProgress);
            if (_resBrView != null)
                _resBrView.Text = PlayerChromeText.FormatVideoInfoDetail(model, _streamSwitchInProgress);

            // Control whether updating overlay should show it. If suppressed (e.g. switching via Right while overlay hidden),
            // update texts but do not reveal the overlay.
            // While video-info is locked open, keep it visible even during buffering so Buffer % can update.
            if (!_suppressOverlayShow)
            {
                if (_isBuffering && !_overlayLocked)
                {
                    HideOverlayAnimated();
                    return;
                }

                if (_isInfoVisible)
                {
                    ShowOverlayAnimated();
                    if (!_overlayLocked) ScheduleHideOverlay();
                }
            }
            else
            {
                // Clear suppression after applying update so future updates behave normally
                _suppressOverlayShow = false;
            }
        }

        /// <summary>Applies the tracker's spinner verdict to the view. Decides nothing itself.</summary>
        private void ApplyWaitIndicator(bool show)
        {
            try
            {
                var indicator = _bufferingIndicator;
                if (indicator == null) return;
                if (_anticipatingToken)
                    show = false;
                var visibility = show ? global::Android.Views.ViewStates.Visible : global::Android.Views.ViewStates.Gone;
                RunOnUiThread(() => indicator.Visibility = visibility);
            }
            catch (Exception ex) { LogIgnored("ApplyWaitIndicator", ex); }
        }
        private void ShowOverlayAnimated()
        {
            try
            {
                var overlay = _overlayContainer;
                if (overlay == null) return;
                RunOnUiThread(() =>
                {
                    // Text refresh (video-info lock, live bitrate) calls this on every tick.
                    // Restarting the fade each time flashes the panel.
                    if (overlay.Visibility == global::Android.Views.ViewStates.Visible && overlay.Alpha >= 0.99f)
                        return;

                    overlay.Animate()?.Cancel();
                    overlay.Visibility = global::Android.Views.ViewStates.Visible;
                    overlay.Animate()?.Alpha(1f)?.SetDuration(200)?.Start();
                });
            }
            catch (Exception ex) { LogIgnored("ShowOverlayAnimated", ex); }
        }

        private void HideOverlayAnimated()
        {
            try
            {
                var overlay = _overlayContainer;
                if (overlay == null) return;
                RunOnUiThread(() =>
                {
                    overlay.Animate()?.Cancel();
                    overlay.Animate()?.Alpha(0f)?.SetDuration(300)?.WithEndAction(new Java.Lang.Runnable(() =>
                    {
                        try
                        {
                            // A show that started after this hide must not be snapped to Gone.
                            if (overlay.Alpha > 0.05f)
                                return;
                            overlay.Visibility = global::Android.Views.ViewStates.Gone;
                        }
                        catch { }
                    }))?.Start();
                });
            }
            catch (Exception ex) { LogIgnored("HideOverlayAnimated", ex); }
        }

        private void ScheduleHideOverlay()
        {
            try
            {
                if (_overlayLocked) return;
                RemoveCallback(_overlayHandler, _overlayHideRunnable);
                PostDelayedCallback(_overlayHandler, _overlayHideRunnable, OverlayTimeoutMs);
            }
            catch (Exception ex) { LogIgnored("ScheduleHideOverlay", ex); }
        }

        private bool TryActivateFocusedMenuItem()
        {
            if (!_isMenuVisible || _menuPanel == null) return false;

            var focusedView = CurrentFocus;
            if (focusedView is not global::Android.Views.View view) return false;
            if (view is not global::Android.Widget.Button button) return false;

            var parent = view.Parent;
            while (parent != null)
            {
                if (ReferenceEquals(parent, _menuPanel))
                {
                    button.PerformClick();
                    return true;
                }

                parent = (parent as global::Android.Views.View)?.Parent;
            }

            return false;
        }

        private void ShowMenu()
        {
            var chrome = EnsureChrome();
            if (!chrome.IsMenuVisible)
                chrome.ToggleMenu();
            else
                ApplyChromeState();
            _videoInfoButton?.Post(() => _videoInfoButton.RequestFocus());
        }

        private void HideMenu() => EnsureChrome().HideMenu();

        private void ShowInfoOverlay()
        {
            try
            {
                RemoveCallback(_streamToastHandler, _streamToastRunnable);
                if (_streamToastView != null)
                    _streamToastView.Visibility = global::Android.Views.ViewStates.Gone;
            }
            catch (Exception ex) { LogIgnored("DismissStreamToast", ex); }
            EnsureChrome().ShowVideoInfo();
        }

        private void HideInfoOverlay() => EnsureChrome().HideVideoInfo();

        private void ShowStreamToastIfNeeded()
        {
            if (_streamToastView == null || _streamToastHandler == null || _streamToastRunnable == null) return;
            if (_switching == null) return;

            var chrome = EnsureChrome();
            if (chrome.IsVideoInfoVisible) return;

            var index = _switching.GetCurrentStreamIndex();
            var total = _switching.GetHealthyStreams().Count;
            if (total <= 0) return;

            if (index == _lastToastIndex && total == _lastToastTotal) return;
            _lastToastIndex = index;
            _lastToastTotal = total;

            var current = _switching.GetCurrentStream();
            string? vertRes = null;
            try
            {
                var res = current?.Health?.Resolution ?? current?.Stream?.Resolution;
                vertRes = PlayerOverlayFormatter.ExtractVerticalResolutionLabel(res);
                if (vertRes == null && _videoHeight.HasValue)
                    vertRes = $"{_videoHeight}p";
            }
            catch (Exception ex) { LogIgnored("ParseToastResolution", ex); }

            chrome.ApplyOverlayInfo(new PlayerOverlayInfo
            {
                Index = index,
                Total = total,
                Resolution = current?.Health?.Resolution ?? current?.Stream?.Resolution ?? vertRes,
                Channel = current?.Stream?.Channel,
                Title = current?.Stream?.Channel
            });

            _streamToastView.Text = chrome.StreamToast?.Text
                ?? PlayerOverlayFormatter.FormatStreamToast(index, total, vertRes);
            _streamToastView.Visibility = global::Android.Views.ViewStates.Visible;
            RemoveCallback(_streamToastHandler, _streamToastRunnable);
            PostDelayedCallback(_streamToastHandler, _streamToastRunnable, (int)PlaybackChromePresenter.StreamToastAutoHide.TotalMilliseconds);
        }

        private void UpdateBackdropVisibility()
        {
            if (_menuBackdrop == null) return;
            var menu = _chrome?.IsMenuVisible ?? _isMenuVisible;
            var info = _chrome?.IsVideoInfoVisible ?? _isInfoVisible;
            _menuBackdrop.Visibility = (menu || info) && !_isTvDevice
                ? global::Android.Views.ViewStates.Visible
                : global::Android.Views.ViewStates.Gone;
        }

        private string BuildOverlayGameTitle(string fallbackChannel)
        {
            if (!string.IsNullOrWhiteSpace(_currentHomeTeam) && !string.IsNullOrWhiteSpace(_currentAwayTeam))
            {
                var international = InternationalTeamDisplay.IsInternationalMatch(
                    _currentLeague, _currentHomeTeam, _currentAwayTeam);
                return InternationalTeamDisplay.FormatMatchTitle(
                    _currentHomeTeam, _currentAwayTeam, international);
            }

            return string.IsNullOrEmpty(_gameTitle) ? fallbackChannel : _gameTitle;
        }
    }
}
#endif
