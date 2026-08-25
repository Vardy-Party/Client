using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Foundation;
using HttpClientWin = Windows.Web.Http.HttpClient;
using MauiApp = Microsoft.Maui.Controls.Application;
using WinButton = Microsoft.UI.Xaml.Controls.Button;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinThickness = Microsoft.UI.Xaml.Thickness;
using WinVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using VardyParty.Extensions;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Playback;
using System.Text.RegularExpressions;

namespace VardyParty.Platforms.Windows
{
    public partial class WindowsVideoPlayerService : INativeVideoPlayerService
    {
        private readonly IStreamSwitchingService _switchingService;
        private readonly IServiceProvider _services;
        private readonly IEnrichedGameService _enrichedGames;
        private readonly IApiService _api;
        private readonly IStreamHealthReporter _healthReporter;
        private readonly ILogger<WindowsVideoPlayerService> _logger;

        public WindowsVideoPlayerService(
            IStreamSwitchingService switchingService,
            IServiceProvider services,
            IEnrichedGameService enrichedGames,
            IApiService api,
            IStreamHealthReporter healthReporter,
            ILogger<WindowsVideoPlayerService> logger)
        {
            _switchingService = switchingService;
            _services = services;
            _enrichedGames = enrichedGames;
            _api = api;
            _healthReporter = healthReporter;
            _logger = logger;
        }

        private void LogIgnored(string operation, Exception ex)
            => _logger.LogDebug(ex, "[WindowsVideoPlayer] {Operation} failed", operation);

        // Segoe UI renders regional-indicator pairs as plain letters; strip them for display.
        // \p{Regional_Indicator} is unavailable on some .NET Windows builds — use UTF-16 ranges.
        private static readonly Regex TickerMeasurePlainTextRegex = new(
            @"\uD83C[\uDDE6-\uDDFF](?:\uD83C[\uDDE6-\uDDFF])?",
            RegexOptions.Compiled);

        private static string ToTickerDisplayText(string text) =>
            TickerMeasurePlainTextRegex.Replace(text, string.Empty);

        private static string TruncateForLog(string text, int maxLength) =>
            text.Length <= maxLength ? text : text[..maxLength] + "…";

        public event EventHandler<bool>? BufferingStateChanged;

        private PlaybackMetrics? _currentMetrics;
        private MediaPlaybackItem? _currentPlaybackItem;

        public PlaybackMetrics? GetCurrentMetrics()
        {
            // Refresh bitrate from adaptive source before returning
            try
            {
                if (_currentPlaybackItem != null)
                {
                    _logger.LogInformation($"GetCurrentMetrics: Refreshing bitrate from adaptive source...");
                    UpdateBitrateFromAdaptiveSource(_currentPlaybackItem);
                }
                else
                {
                    _logger.LogInformation($"GetCurrentMetrics: No current playback item available");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetCurrentMetrics: Failed to update bitrate");
            }

            _logger.LogInformation($"GetCurrentMetrics returning: Resolution={_currentMetrics?.Resolution}, Bitrate={_currentMetrics?.BitrateKbps}, Framerate={_currentMetrics?.Framerate}");
            return _currentMetrics;
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
            var tcs = new TaskCompletionSource<PlaybackResult>();

            // Block Blazor renders before any UI-thread work is queued — progress updates can
            // still fire on a background thread after the first healthy stream is found.
            MainPage.SetNativePlayerActive(true);
            _logger.LogInformation($"PlayVideoAsync starting: {title}");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    new PlayerSession(
                        this,
                        m3u8Url,
                        refererUrl,
                        title,
                        onNextStreamRequested,
                        league,
                        homeTeam,
                        awayTeam,
                        requestHeaders,
                        tcs).Run();
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "UI thread setup failed");
                    MainPage.SetNativePlayerActive(false);
                    tcs.TrySetResult(PlaybackResult.Completed($"Player UI failed: {ex.Message}", true));
                }
            });

            return tcs.Task;
        }

        public class VardyPartyWindow : MauiWinUIWindow
        {
        }
    }
}

