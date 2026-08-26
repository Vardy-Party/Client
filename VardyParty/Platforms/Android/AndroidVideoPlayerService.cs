#if ANDROID
using System;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;

namespace VardyParty.Platforms.Android
{
    public class AndroidVideoPlayerService : INativeVideoPlayerService
    {
        public event EventHandler<bool>? BufferingStateChanged;

        private static AndroidVideoPlayerService? _instance;
        private static TaskCompletionSource<PlaybackResult>? _playbackTcs;
        private static Func<Task>? _onNextStreamRequested;
        private static PlaybackMetrics? _currentMetrics;

        public AndroidVideoPlayerService()
        {
            _instance = this;
        }

        public Task<PlaybackResult> PlayVideoAsync(
            string m3u8Url,
            string refererUrl,
            string title,
            Func<Task>? onNextStreamRequested = null,
            string? league = null,
            string? homeTeam = null,
            string? awayTeam = null,
            IReadOnlyDictionary<string, string>? requestHeaders = null)
        {
            try
            {
                var context = global::Android.App.Application.Context;
                if (context == null) return Task.FromResult(PlaybackResult.Completed("No context", true));

                _playbackTcs = new TaskCompletionSource<PlaybackResult>();
                _onNextStreamRequested = onNextStreamRequested;

                var intent = new global::Android.Content.Intent(context, typeof(NativeVideoActivity));
                intent.PutExtra("M3U8_URL", m3u8Url);
                intent.PutExtra("REFERER_URL", refererUrl);
                // Pass the game title (prefer BBC names) if available
                intent.PutExtra("TITLE", title);
                intent.PutExtra("LEAGUE", league ?? string.Empty);
                intent.PutExtra("HOME_TEAM", homeTeam ?? string.Empty);
                intent.PutExtra("AWAY_TEAM", awayTeam ?? string.Empty);
                // Prefer starting the native activity in the current activity/task so back navigation
                // returns to the app Home page correctly. Fall back to NewTask when no current
                // activity is available (e.g., background context).
                try
                {
                    var currentActivity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                    if (currentActivity != null)
                    {
                        // Start in same task/activity
                        currentActivity.StartActivity(intent);
                    }
                    else
                    {
                        intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
                        context.StartActivity(intent);
                    }
                }
                catch
                {
                    // Best-effort: fallback to NewTask if anything goes wrong
                    try
                    {
                        intent.SetFlags(global::Android.Content.ActivityFlags.NewTask);
                        context.StartActivity(intent);
                    }
                    catch { }
                }

                // Return task which will be signaled by NativeVideoActivity.ReportPlaybackResult
                return _playbackTcs.Task;
            }
            catch (Exception ex)
            {
                return Task.FromResult(PlaybackResult.Completed(ex.Message, true));
            }
        }

        internal static void ReportPlaybackResult(PlaybackResult result)
        {
            try
            {
                _playbackTcs?.TrySetResult(result);
            }
            catch { }
            _playbackTcs = null;
            _onNextStreamRequested = null;
        }

        internal static async Task RequestNextStream()
        {
            if (_onNextStreamRequested != null)
            {
                await _onNextStreamRequested();
                return;
            }

            try
            {
                var switching = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Ports.IStreamSwitchingService)) as VardyParty.Ports.IStreamSwitchingService;
                switching?.SwitchToNextStream();
            }
            catch { }
        }

        internal static void ReportBufferingState(bool isBuffering)
        {
            try
            {
                _instance?.BufferingStateChanged?.Invoke(_instance, isBuffering);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AndroidVideoPlayerService] ReportBufferingState failed: {ex.Message}");
            }
        }

        public PlaybackMetrics? GetCurrentMetrics()
        {
            // Return cached metrics from the native activity
            return _currentMetrics;
        }

        /// <summary>
        /// Called by NativeVideoActivity to update current playback metrics
        /// </summary>
        internal static void UpdateMetrics(PlaybackMetrics? metrics)
        {
            _currentMetrics = metrics;
        }
    }
}
#endif
