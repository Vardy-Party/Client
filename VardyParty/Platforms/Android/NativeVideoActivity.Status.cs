#if ANDROID
using System;
using Microsoft.Extensions.Logging;
using VardyParty.Playback;

namespace VardyParty.Platforms.Android
{
    /// <summary>
    /// Single source of truth for the status text ("Playing"/"Buffering"/...), the buffering spinner and
    /// the overlay visibility. All of it comes from one <see cref="PlaybackStatusTracker.Assess"/> call on
    /// every player event and on a periodic watchdog tick, so a missed or reordered event can no longer
    /// leave "Buffering" latched while the stream plays, and the spinner is never derived a second time.
    /// This file only reads the player and applies the verdict; every decision lives in VardyParty.Playback
    /// and is unit-tested there.
    /// </summary>
    public partial class NativeVideoActivity
    {
        private const int StatusWatchdogIntervalMs = 500;

        /// <summary>
        /// Set as soon as the viewer leaves (Back, Finish, destroy). From then on no wait chrome may appear
        /// and no player callback may touch the UI: Stop() drops the player to IDLE, which would otherwise
        /// re-show the spinner over a closing activity.
        /// </summary>
        private PlaybackLifecycle _lifecycle = PlaybackLifecycle.Active;

        private bool IsLifecycleClosing => _lifecycle == PlaybackLifecycle.Closing;

        /// <summary>
        /// Marks the activity as closing and silences everything that reacts to the player: watchdog off,
        /// listener detached, spinner hidden. Idempotent, and must run before the player is stopped.
        /// </summary>
        private void BeginClosing()
        {
            if (IsLifecycleClosing) return;
            _lifecycle = PlaybackLifecycle.Closing;
            StopStatusWatchdog();
            DetachPlayerListener();
            ApplyWaitIndicator(false);
        }

        /// <summary>
        /// Non-finishing pause (Home, another app, a TV system overlay). Stop() is about to drop the player to
        /// IDLE on purpose, so before that: suspend (no wait chrome, watchdog off, listener detached, spinner
        /// hidden). A finishing pause closes instead. <see cref="ResumePlaybackAfterSuspend"/> brings it back.
        /// </summary>
        private void BeginSuspend()
        {
            var next = PlaybackLifecycleRules.OnPause(_lifecycle, IsFinishing);
            if (next == PlaybackLifecycle.Closing)
            {
                BeginClosing();
                return;
            }

            if (_lifecycle == PlaybackLifecycle.Suspended) return;
            _lifecycle = PlaybackLifecycle.Suspended;
            _isPreparing = false;
            StopStatusWatchdog();
            DetachPlayerListener();
            ApplyWaitIndicator(false);
        }

        /// <summary>
        /// Back in the foreground after a suspend: the player was stopped on purpose, so re-attach the current
        /// stream. AttachEngine re-adds the listener, restarts the watchdog and raises the spinner only while the
        /// prepare is in flight. The decision is <see cref="PlaybackLifecycleRules.OnResumeAction"/>.
        /// </summary>
        private void ResumePlaybackAfterSuspend()
        {
            try
            {
                var before = _lifecycle;
                _lifecycle = PlaybackLifecycleRules.OnResume(before);
                var action = PlaybackLifecycleRules.OnResumeAction(before, _player != null, !string.IsNullOrEmpty(_m3u8Url));
                if (action != ResumeAction.Reattach) return;

                _logger?.LogInformation("[NativeVideoActivity] Resumed after suspend - re-attaching {Url}", _m3u8Url);
                AttachEngine(_m3u8Url);
            }
            catch (Exception ex) { LogIgnored("ResumePlaybackAfterSuspend", ex); }
        }

        private void DetachPlayerListener()
        {
            try
            {
                var listener = _playerListener;
                if (listener != null) _player?.RemoveListener(listener);
            }
            catch (Exception ex) { LogIgnored("DetachPlayerListener", ex); }
        }

        /// <summary>Re-derives the status from the player and applies it. Marshals to the main thread when needed.</summary>
        private void RefreshPlaybackStatus()
        {
            try
            {
                // Media3 players may only be read on their application thread (the main looper here).
                if (global::Android.OS.Looper.MyLooper() != global::Android.OS.Looper.MainLooper)
                {
                    RunOnUiThread(RefreshPlaybackStatus);
                    return;
                }

                var player = _player;
                var snapshot = player == null
                    ? new PlayerSnapshot(PlaybackStatusTracker.StateIdle, 0, 0, _playerFaulted)
                    : new PlayerSnapshot(
                        player.PlaybackState,
                        player.BufferedPercentage,
                        player.CurrentPosition,
                        _playerFaulted);

                var view = _statusTracker.Assess(
                    snapshot,
                    global::Android.OS.SystemClock.ElapsedRealtime(),
                    _isPreparing,
                    PlaybackLifecycleRules.SuppressesWaitChrome(_lifecycle));
                ApplyPlaybackStatus(view);
            }
            catch (Exception ex) { LogIgnored("RefreshPlaybackStatus", ex); }
        }

        private void ApplyPlaybackStatus(PlaybackStatusView view)
        {
            var text = view.Status switch
            {
                PlaybackStatus.Idle => VardyParty.Resources.Strings.Resources.StatusIdle,
                PlaybackStatus.Buffering => VardyParty.Resources.Strings.Resources.StatusBuffering,
                PlaybackStatus.Ended => VardyParty.Resources.Strings.Resources.StatusEnded,
                PlaybackStatus.Error => VardyParty.Resources.Strings.Resources.StatusError,
                _ => VardyParty.Resources.Strings.Resources.StatusPlaying
            };

            var buffering = view.Status == PlaybackStatus.Buffering;
            var bufferingChanged = buffering != _isBuffering;
            var textChanged = !string.Equals(text, _playbackStateText, StringComparison.Ordinal);

            _playbackStateText = text;
            _isBuffering = buffering;

            // The tracker's verdict is applied as-is. It is re-applied on every derivation so a stray
            // visible spinner is always cleared, and a closing activity never shows one.
            ApplyWaitIndicator(view.ShowSpinner);
            if (buffering && bufferingChanged) HideOverlayAnimated();

            // Only genuine stall start / recovery reach the engine (see PlaybackStatusTracker.SignalFor).
            if (!PlaybackLifecycleRules.SuppressesWaitChrome(_lifecycle))
            {
                switch (view.Signal)
                {
                    case BufferingSignal.Started:
                        _engine.Raise(MediaEngineEvent.Buffering(CurrentAttachGeneration, true));
                        break;
                    case BufferingSignal.Recovered:
                        _engine.Raise(MediaEngineEvent.Buffering(CurrentAttachGeneration, false));
                        break;
                }
            }

            if (textChanged || bufferingChanged)
            {
                var last = _lastOverlayInfo;
                if (last != null) RunOnUiThread(() => UpdateOverlayText(last));
            }
        }

        private void StartStatusWatchdog()
        {
            if (_statusHandler != null || PlaybackLifecycleRules.SuppressesWaitChrome(_lifecycle)) return;

            // Locals, not fields: Stop can null the fields between a tick being queued and it running.
            var handler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            Java.Lang.Runnable? runnable = null;
            runnable = new Java.Lang.Runnable(() =>
            {
                var live = ReferenceEquals(_statusHandler, handler);
                try
                {
                    if (!live || PlaybackLifecycleRules.SuppressesWaitChrome(_lifecycle) || IsFinishing || IsDestroyed) return;
                    RefreshPlaybackStatus();
                    // Until a picture shows, let the session time out an audio-only stream.
                    ProbeVideo();
                }
                catch (Exception ex) { LogIgnored("StatusWatchdogTick", ex); }
                finally
                {
                    // Keep ticking through a failed tick, but only while this watchdog is still the live one.
                    if (live && ReferenceEquals(_statusHandler, handler) && !PlaybackLifecycleRules.SuppressesWaitChrome(_lifecycle) && !IsFinishing && !IsDestroyed)
                    {
                        try { handler.PostDelayed(runnable!, StatusWatchdogIntervalMs); }
                        catch (Exception ex) { LogIgnored("StatusWatchdogReschedule", ex); }
                    }
                }
            });

            _statusHandler = handler;
            _statusRunnable = runnable;
            handler.PostDelayed(runnable, StatusWatchdogIntervalMs);
        }

        private void StopStatusWatchdog()
        {
            var handler = _statusHandler;
            var runnable = _statusRunnable;
            _statusHandler = null;
            _statusRunnable = null;
            try
            {
                if (handler != null && runnable != null)
                    handler.RemoveCallbacks(runnable);
            }
            catch (Exception ex) { LogIgnored("StopStatusWatchdog", ex); }
        }
    }
}
#endif
