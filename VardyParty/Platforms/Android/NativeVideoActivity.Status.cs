#if ANDROID
using System;
using VardyParty.Playback;

namespace VardyParty.Platforms.Android
{
    /// <summary>
    /// Single source of truth for the status text ("Playing"/"Buffering"/...), the buffering spinner and
    /// the overlay visibility. All three are derived from the live player state by
    /// <see cref="PlaybackStatusTracker"/> on every player event and on a periodic watchdog tick, so a
    /// missed or reordered event can no longer leave "Buffering" latched while the stream plays.
    /// </summary>
    public partial class NativeVideoActivity
    {
        private const int StatusWatchdogIntervalMs = 500;

        /// <summary>Re-derives the status from the player and applies it. Must run on the main thread.</summary>
        private void RefreshPlaybackStatus()
        {
            try
            {
                var player = _player;
                var snapshot = player == null
                    ? new PlayerSnapshot(PlaybackStatusTracker.StateIdle, false, 0, 0, _playerFaulted)
                    : new PlayerSnapshot(
                        player.PlaybackState,
                        player.IsPlaying,
                        player.BufferedPercentage,
                        player.CurrentPosition,
                        _playerFaulted);

                var status = _statusTracker.Evaluate(snapshot, global::Android.OS.SystemClock.ElapsedRealtime());
                ApplyPlaybackStatus(status);
            }
            catch (Exception ex) { LogIgnored("RefreshPlaybackStatus", ex); }
        }

        private void ApplyPlaybackStatus(PlaybackStatus status)
        {
            var text = status switch
            {
                PlaybackStatus.Idle => VardyParty.Resources.Strings.Resources.StatusIdle,
                PlaybackStatus.Buffering => VardyParty.Resources.Strings.Resources.StatusBuffering,
                PlaybackStatus.Ended => VardyParty.Resources.Strings.Resources.StatusEnded,
                PlaybackStatus.Error => VardyParty.Resources.Strings.Resources.StatusError,
                _ => VardyParty.Resources.Strings.Resources.StatusPlaying
            };

            var buffering = status == PlaybackStatus.Buffering;
            var bufferingChanged = buffering != _isBuffering;
            var textChanged = !string.Equals(text, _playbackStateText, StringComparison.Ordinal);

            _playbackStateText = text;
            _isBuffering = buffering;

            if (buffering)
            {
                ShowBufferingIndicator();
                if (bufferingChanged) HideOverlayAnimated();
            }
            else
            {
                // Always hide (not only on a transition): cheap, and guarantees a stray visible spinner is cleared.
                HideBufferingIndicator();
            }

            if (bufferingChanged)
                _engine.Raise(MediaEngineEvent.Buffering(CurrentAttachGeneration, buffering));

            if (textChanged || bufferingChanged)
            {
                var last = _lastOverlayInfo;
                if (last != null) RunOnUiThread(() => UpdateOverlayText(last));
            }
        }

        private void StartStatusWatchdog()
        {
            if (_statusHandler != null) return;
            _statusHandler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _statusRunnable = new Java.Lang.Runnable(() =>
            {
                if (_statusHandler == null || _statusRunnable == null) return;
                RefreshPlaybackStatus();
                _statusHandler?.PostDelayed(_statusRunnable, StatusWatchdogIntervalMs);
            });
            _statusHandler.PostDelayed(_statusRunnable, StatusWatchdogIntervalMs);
        }

        private void StopStatusWatchdog()
        {
            try
            {
                if (_statusHandler != null && _statusRunnable != null)
                    _statusHandler.RemoveCallbacks(_statusRunnable);
            }
            catch (Exception ex) { LogIgnored("StopStatusWatchdog", ex); }
            _statusHandler = null;
            _statusRunnable = null;
        }
    }
}
#endif