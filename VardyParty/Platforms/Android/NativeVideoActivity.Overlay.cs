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

        private PlayerOverlayInfo? BuildOverlayInfoFromCurrentStream()
        {
            try
            {
                var current = _switching?.GetCurrentStream();
                if (current == null)
                {
                    return null;
                }

                return new PlayerOverlayInfo
                {
                    Index = _switching?.GetCurrentStreamIndex() ?? 0,
                    Total = _switching?.GetHealthyStreams().Count ?? 0,
                    Channel = current.Stream?.Channel,
                    BitrateKbps = current.Stream?.BitrateKbps ?? current.Health?.Bitrate,
                    Resolution = current.Stream?.Resolution ?? current.Health?.Resolution,
                    M3u8Url = current.ResolvedM3U8Url ?? _m3u8Url,
                    RefererUrl = _refererUrl,
                    BufferPercent = _player?.BufferedPercentage,
                    FrameRate = current.Health?.FrameRate != null ? (double?)current.Health.FrameRate : null,
                    VideoCodec = PlayerOverlayFormatter.MapCodecToFriendlyName(current.Health?.VideoCodec),
                    AudioCodec = PlayerOverlayFormatter.MapCodecToFriendlyName(current.Health?.AudioCodec),
                    AspectRatio = PlayerOverlayFormatter.BuildAspect(current.Stream?.Resolution ?? current.Health?.Resolution),
                    Title = current.Stream?.Channel
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
                if (_sourceBadgeView != null) _sourceBadgeView.Visibility = global::Android.Views.ViewStates.Gone;
                if (_qualityView != null) _qualityView.Text = string.Empty;
                if (_resBrView != null) _resBrView.Text = string.Empty;
                return;
            }

            _lastOverlayInfo = info;

            // Top line should be game title (Home vs Away) when provided
            var channel = info.Title ?? VardyParty.Resources.Strings.Resources.UnknownChannel;
            var statusLine = $"{VardyParty.Resources.Strings.Resources.StatusLabel}: {_playbackStateText}";
            var indexLine = info.Total > 0 ? string.Format(VardyParty.Resources.Strings.Resources.StreamIndexFormat, info.Index, info.Total) : string.Empty;

            // Try to obtain a quality/health label from the current enriched stream if present
            string qualityLabel = string.Empty;
            try
            {
                var current = _switching?.GetCurrentStream();
                if (current != null)
                {
                    qualityLabel = current.GetQualityDisplay();
                }
            }
            catch (Exception ex) { LogIgnored("GetQualityDisplay", ex); }

            var resolution = info.Resolution ?? string.Empty;
            var aspectRatio = info.AspectRatio ?? string.Empty;
            var br = info.BitrateKbps.HasValue ? $"{info.BitrateKbps} kbps" : string.Empty;
            var fr = info.FrameRate.HasValue ? $"{info.FrameRate:0.##} fps" : string.Empty;
            var buf = info.BufferPercent.HasValue ? $"Buffer {info.BufferPercent}%" : string.Empty;
            var m3u8 = info.M3u8Url ?? string.Empty;
            var referer = info.RefererUrl ?? string.Empty;
            if (!string.IsNullOrEmpty(m3u8)) m3u8 = $"Source: {PlayerOverlayFormatter.StripQuery(m3u8)}";
            if (!string.IsNullOrEmpty(referer)) referer = $"Referer: {PlayerOverlayFormatter.RefererHost(referer)}";
            // If video surface size is available, include resolution/framerate placeholder
            var resDetails = string.Empty;
            try
            {
                if (_videoWidth.HasValue && _videoHeight.HasValue)
                {
                    resDetails = $"{_videoWidth}x{_videoHeight}";
                }
            }
            catch (Exception ex) { LogIgnored("ReadVideoSize", ex); }

            if (_titleView != null) _titleView.Text = BuildOverlayGameTitle(channel);
            if (_statusView != null) _statusView.Text = statusLine;
            if (_indexView != null) _indexView.Text = indexLine;
            ApplySourceBadge(_switching?.GetCurrentStream()?.Stream?.CatalogSourceBadgeLabel);
            if (_qualityView != null) _qualityView.Text = qualityLabel;
            // Build lines: resolution (+fr), bitrate, buffer each on its own line
            var lines = new List<string>();
            var resLine = string.Empty;
            if (!string.IsNullOrEmpty(resDetails) || !string.IsNullOrEmpty(resolution))
            {
                // Frame rate appears after an @ symbol following resolution
                if (!string.IsNullOrEmpty(fr) && !string.IsNullOrEmpty(resolution))
                    resLine = $"{resolution} @ {info.FrameRate:0.##} fps";
                else if (!string.IsNullOrEmpty(resolution))
                    resLine = resolution;
                else if (!string.IsNullOrEmpty(resDetails))
                    resLine = resDetails;
            }
            if (!string.IsNullOrEmpty(resLine)) lines.Add(resLine);
            if (!string.IsNullOrEmpty(aspectRatio)) lines.Add($"Aspect ratio: {aspectRatio}");
            if (!string.IsNullOrEmpty(br)) lines.Add(br);
            if (!string.IsNullOrEmpty(buf)) lines.Add(buf);

            // Codec line
            var codecLineParts = new List<string>();
            if (!string.IsNullOrEmpty(info.VideoCodec)) codecLineParts.Add($"Video: {info.VideoCodec}");
            if (!string.IsNullOrEmpty(info.AudioCodec)) codecLineParts.Add($"Audio: {info.AudioCodec}");
            if (codecLineParts.Count > 0) lines.Add(string.Join(" / ", codecLineParts));

            // m3u8 and referer each on their own lines
            if (!string.IsNullOrEmpty(m3u8)) lines.Add(m3u8);
            if (!string.IsNullOrEmpty(referer)) lines.Add(referer);

            if (_resBrView != null) _resBrView.Text = string.Join("\n", lines);

            // Control whether updating overlay should show it. If suppressed (e.g. switching via Right while overlay hidden),
            // update texts but do not reveal the overlay.
            if (!_suppressOverlayShow)
            {
                if (_isBuffering)
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

        private void RefreshWaitIndicator()
        {
            try
            {
                var state = _player?.PlaybackState ?? PlayerStateIdle;
                var show = PlaybackWaitChrome.ShouldShowWaitIndicator(
                    isReady: state == PlayerStateReady,
                    isLoading: _isBuffering,
                    isPreparing: _isPreparing,
                    isEnded: state == PlayerStateEnded);
                if (show)
                    ShowBufferingIndicator();
                else
                    HideBufferingIndicator();
            }
            catch (Exception ex) { LogIgnored("RefreshWaitIndicator", ex); }
        }

        private void ShowBufferingIndicator()
        {
            try
            {
                if (_bufferingIndicator == null) return;
                RunOnUiThread(() => _bufferingIndicator.Visibility = global::Android.Views.ViewStates.Visible);
            }
            catch (Exception ex) { LogIgnored("ShowBufferingIndicator", ex); }
        }

        private void HideBufferingIndicator()
        {
            try
            {
                if (_bufferingIndicator == null) return;
                RunOnUiThread(() => _bufferingIndicator.Visibility = global::Android.Views.ViewStates.Gone);
            }
            catch (Exception ex) { LogIgnored("HideBufferingIndicator", ex); }
        }

        private void ShowOverlayAnimated()
        {
            try
            {
                var overlay = _overlayContainer;
                if (overlay == null) return;
                RunOnUiThread(() =>
                {
                    overlay.Animate()?.Cancel();
                    overlay.Visibility = global::Android.Views.ViewStates.Visible;
                    overlay.Alpha = 0f;
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
                        try { overlay.Visibility = global::Android.Views.ViewStates.Gone; } catch { }
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
                var home = FormatTeamForDisplay(_currentHomeTeam, international);
                var away = FormatTeamForDisplay(_currentAwayTeam, international);
                return InternationalTeamDisplay.FormatMatchTitle(home, away, international: false);
            }

            return string.IsNullOrEmpty(_gameTitle) ? fallbackChannel : _gameTitle;
        }
    }
}
#endif
