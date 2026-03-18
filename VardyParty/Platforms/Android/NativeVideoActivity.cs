#if ANDROID
using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Hls;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.Common;
using AndroidX.Media3.UI;
using VardyParty.Services;
using VardyParty.Models;
using Microsoft.Extensions.Logging;
using System.Threading;
using VardyParty.Health;

namespace VardyParty.Platforms.Android
{
    [Activity(Label = "Video Player", Theme = "@style/Maui.MainTheme", MainLauncher = false, ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Landscape)]
    public class NativeVideoActivity : Activity
    {
        // Constructor DI - OS will call default ctor which chains to parameterized ctor
        public NativeVideoActivity() : this(ResolveSwitching(), ResolveLogger()) { }

        public NativeVideoActivity(IStreamSwitchingService? switching, ILogger<NativeVideoActivity>? logger)
        {
            _switching = switching;
            _logger = logger;
            _healthReporter = ResolveHealthReporter();
        }

        private static string? MapCodecToFriendlyName(string? codec)
        {
            if (string.IsNullOrEmpty(codec)) return null;
            try
            {
                var lower = codec.ToLowerInvariant();
                if (lower.StartsWith("avc1") || lower.StartsWith("avc3") || lower.Contains("h264") || lower.Contains("avc")) return "H.264";
                if (lower.StartsWith("hev1") || lower.StartsWith("hvc1") || lower.Contains("hevc") || lower.Contains("h265")) return "H.265";
                if (lower.StartsWith("vp9") || lower.Contains("vp9")) return "VP9";
                if (lower.StartsWith("vp8") || lower.Contains("vp8")) return "VP8";
                if (lower.StartsWith("mp4a") || lower.Contains("aac") || lower.Contains("mp4a")) return "AAC";
                if (lower.StartsWith("ac-3") || lower.Contains("ac3")) return "AC-3";
                if (lower.StartsWith("opus") || lower.Contains("opus")) return "Opus";
                // default: return original token trimmed
                return codec;
            }
            catch { return codec; }
        }

        private async Task AttemptManifestFallbackAsync(string m3u8Url)
        {
            try
            {
                _logger?.LogInformation("[NativeVideoActivity] Attempting manifest fallback for {Url}", m3u8Url);
                // Download manifest and save to temp file, then try to play from file:// URI
                using var http = new System.Net.Http.HttpClient();
                // Use a browser-like UA consistently
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                if (!string.IsNullOrEmpty(_refererUrl))
                {
                    if (Uri.TryCreate(_refererUrl, UriKind.Absolute, out var ruri))
                        http.DefaultRequestHeaders.Referrer = ruri;
                }

                var resp = await http.GetAsync(m3u8Url);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger?.LogWarning("[NativeVideoActivity] Manifest fallback download failed: {Status}", resp.StatusCode);
                    try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Manifest fallback failed" }); } catch { }
                    return;
                }

                var content = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger?.LogWarning("[NativeVideoActivity] Manifest fallback empty content");
                    try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Manifest empty" }); } catch { }
                    return;
                }

                // Store manifest in-memory and serve it via intercepting data source so we never use file://
                try
                {
                    _inMemoryManifestMap ??= new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
                    var bytes = System.Text.Encoding.UTF8.GetBytes(content);
                    _inMemoryManifestMap[m3u8Url] = bytes;

                    // Build manifest entry map expected by factory
                    var manifestMap = new System.Collections.Generic.Dictionary<string, ManifestCacheEntry>();
                    foreach (var kv in _inMemoryManifestMap)
                    {
                        manifestMap[kv.Key] = new ManifestCacheEntry { Data = kv.Value, Added = DateTimeOffset.UtcNow };
                    }

                    RunOnUiThread(() =>
                    {
                        try
                        {
                            var headers = new System.Collections.Generic.Dictionary<string, string?>
                            {
                                ["Referer"] = _refererUrl ?? string.Empty,
                                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
                            };
                            var headerFactory = new HeaderInjectingDataSourceFactory(headers);
                            var interceptFactory = new InMemoryInterceptingDataSourceFactory(headerFactory, manifestMap, maxEntries: 5, maxAge: TimeSpan.FromSeconds(60));
                            var mediaItem = new MediaItem.Builder().SetUri(m3u8Url).SetMimeType(MimeTypes.ApplicationM3u8).Build();
                            var mediaSource = new HlsMediaSource.Factory(interceptFactory).CreateMediaSource(mediaItem);
                            _player?.SetMediaSource(mediaSource);
                            _player?.Prepare();
                            _player?.Play();
                            _logger?.LogInformation("[NativeVideoActivity] Serving manifest from memory for {Url}", m3u8Url);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "[NativeVideoActivity] Fallback play failed (in-memory)");
                            try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Fallback play failed" }); } catch { }
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[NativeVideoActivity] Manifest fallback in-memory exception");
                    try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Manifest fallback exception" }); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Manifest fallback exception");
                try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Manifest fallback exception" }); } catch { }
            }
        }
        // No changes made to the file.

        // Support post-construction injection for true constructor-like DI via activity factory / lifecycle hook
        public void InjectServices(IStreamSwitchingService? switching, ILogger<NativeVideoActivity>? logger)
        {
            try
            {
                if (switching != null) _switching = switching;
                if (logger != null) _logger = logger;
            }
            catch { }
        }

        // Test helpers / query methods to allow unit testing decision logic without running Android UI
        public void SetPreparingForTests(bool preparing) => _isPreparing = preparing;
        public void SetCurrentPlaybackUrlForTests(string? url) => _m3u8Url = url ?? string.Empty;
        public bool CanSwitchTo(string candidateUrl)
        {
            if (string.IsNullOrEmpty(candidateUrl)) return false;
            if (_isPreparing) return false;
            if (string.Equals(_m3u8Url, candidateUrl, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static IStreamSwitchingService? ResolveSwitching()
        {
            try { return VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(IStreamSwitchingService)) as IStreamSwitchingService; } catch { return null; }
        }

        private static ILogger<NativeVideoActivity>? ResolveLogger()
        {
            try
            {
                var lf = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                return lf?.CreateLogger<NativeVideoActivity>();
            }
            catch { return null; }
        }

        private IExoPlayer? _player;
        private PlayerView? _playerView;
        private TextView? _titleView;
        private TextView? _statusView;
        private TextView? _indexView;
        private TextView? _qualityView;
        private TextView? _resBrView;
        private global::Android.Views.View? _overlayContainer;
        private global::Android.Widget.ProgressBar? _bufferingIndicator;
        private global::Android.OS.Handler? _overlayHandler;
        private Java.Lang.IRunnable? _overlayHideRunnable;
        private const int OverlayTimeoutMs = 3000;
        private bool _overlayLocked;
        private bool _isBuffering;
        private string? _gameTitle;
        private bool _suppressOverlayShow;
        private int? _videoWidth;
        private int? _videoHeight;
        private string _m3u8Url = string.Empty;
        private string? _refererUrl;
        private IStreamSwitchingService? _switching;
        private ILogger<NativeVideoActivity>? _logger;
        private IStreamHealthReporter? _healthReporter;
        private StreamMetricsWindow _metricsWindow = new();
        private Timer? _healthReportTimer;
        private bool _isPreparing;
        private IDisposable? _healthySub;
        private IDisposable? _indexSub;
        private IDisposable? _gamesSub;
        private readonly object _gamesLock = new();
        private Dictionary<string, List<Game>>? _latestGamesByLeague;
        private string _currentLeague = string.Empty;
        private string _currentHomeTeam = string.Empty;
        private string _currentAwayTeam = string.Empty;
        private bool _manifestFallbackAttempted;
        private System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>? _inMemoryManifestMap;
        private string _playbackStateText = VardyParty.Resources.Strings.Resources.StatusPlaying;
        private VardyParty.Models.PlayerOverlayInfo? _lastOverlayInfo;
        private global::Android.Widget.ImageButton? _menuButton;
        private LinearLayout? _menuPanel;
        private global::Android.Views.View? _menuBackdrop;
        private TextView? _reportStatusView;
        private global::Android.Widget.Button? _reportButton;
        private global::Android.Widget.Button? _videoInfoButton;
        private LinearLayout? _scoresTickerContainer;
        private TextView? _scoresTickerText;
        private bool _isScoresTickerVisible;
        private bool _isMenuVisible;
        private bool _isInfoVisible;
        private bool _isTvDevice;
        private const int PlayerStateIdle = 1;
        private const int PlayerStateBuffering = 2;
        private const int PlayerStateReady = 3;
        private const int PlayerStateEnded = 4;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Hide system UI (status bar and navigation bar) for full-screen video experience
            HideSystemUI();

            // Allow activity factory to inject services for constructor-like DI
            try { AndroidActivityFactory.Inject(this); } catch { }

            _isTvDevice = MauiProgram.IsTv;

            // Services are injected by AndroidActivityFactory during activity creation; no fallback here.

            _m3u8Url = Intent?.GetStringExtra("M3U8_URL") ?? string.Empty;
            // Game title (prefer BBC display names) passed from caller route
            _gameTitle = Intent?.GetStringExtra("TITLE") ?? string.Empty;
            _refererUrl = Intent?.GetStringExtra("REFERER_URL") ?? string.Empty;
            _currentLeague = Intent?.GetStringExtra("LEAGUE") ?? string.Empty;
            _currentHomeTeam = Intent?.GetStringExtra("HOME_TEAM") ?? string.Empty;
            _currentAwayTeam = Intent?.GetStringExtra("AWAY_TEAM") ?? string.Empty;

            // Basic UI: PlayerView with overlay
            _player = new ExoPlayerBuilder(this).Build();
            _playerView = new PlayerView(this) { Player = _player };
            // disable built-in controller (remove play/prev/next/seek UI)
            try { _playerView.UseController = false; } catch { }
            
            // Wrap PlayerView in a container for proper pinch-to-zoom
            var playerContainer = new FrameLayout(this);
            playerContainer.AddView(_playerView);
            
            // Enable pinch-to-zoom for phone users (Lubo!)
            SetupPinchZoom(playerContainer, _playerView);

            var root = new FrameLayout(this);
            root.SetBackgroundColor(global::Android.Graphics.Color.Black);
            root.AddView(playerContainer);

            // Overlay panel with separate styled lines for better readability and localization
            // Scale UI values using device density to improve readability across devices
            var metrics = Resources.DisplayMetrics;
            float density = metrics.Density; // dp scaling
            float scaledDensity = metrics.ScaledDensity; // sp scaling for fonts

            // Use conservative base sizes and avoid upscaling on TV so overlay fits
            float fontScaleCap = Math.Min(1.0f, scaledDensity); // do not upscale
            float titleSp = 16f * fontScaleCap;
            float bodySp = 12f * fontScaleCap;
            float smallSp = 10f * fontScaleCap;

            int paddingDp = (int)(8 * density);
            int outerLeftDp = (int)(20 * density);
            int outerTopDp = (int)(20 * density);

            // Title will show the game (Home vs Away) at top
            _titleView = new TextView(this) { Text = string.Empty };
            _titleView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, titleSp);
            _titleView.SetTypeface(global::Android.Graphics.Typeface.DefaultBold, global::Android.Graphics.TypefaceStyle.Bold);
            _titleView.SetTextColor(global::Android.Graphics.Color.White);

            // Reduced body fonts for tighter layout
            _statusView = new TextView(this);
            // Use consistent body font for status/index/quality lines
            _statusView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bodySp);
            _statusView.SetTextColor(global::Android.Graphics.Color.White);

            _indexView = new TextView(this);
            _indexView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bodySp);
            _indexView.SetTextColor(global::Android.Graphics.Color.White);

            _qualityView = new TextView(this);
            _qualityView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bodySp);
            _qualityView.SetTextColor(global::Android.Graphics.Color.White);

            _resBrView = new TextView(this);
            // Stream detail (resolution/bitrate/codecs/urls) should use a smaller font
            _resBrView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, smallSp);
            _resBrView.SetTextColor(global::Android.Graphics.Color.LightGray);

            var linear = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical
            };
            // store overlay container to control visibility/animations
            _overlayContainer = linear;
            linear.SetBackgroundDrawable(new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#99000000")));
            linear.AddView(_titleView);
            linear.AddView(_statusView);
            linear.AddView(_indexView);
            linear.AddView(_qualityView);
            linear.AddView(_resBrView);
            linear.Alpha = 0.95f;
            linear.SetPadding(paddingDp, paddingDp, paddingDp, paddingDp);

            var overlayParams = new FrameLayout.LayoutParams(global::Android.Views.ViewGroup.LayoutParams.WrapContent, global::Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = global::Android.Views.GravityFlags.Top | global::Android.Views.GravityFlags.Left,
                LeftMargin = outerLeftDp,
                TopMargin = outerTopDp
            };
            root.AddView(linear, overlayParams);

            _bufferingIndicator = new global::Android.Widget.ProgressBar(this)
            {
                Indeterminate = true,
                Visibility = global::Android.Views.ViewStates.Gone
            };
            var bufferingParams = new FrameLayout.LayoutParams(
                global::Android.Views.ViewGroup.LayoutParams.WrapContent,
                global::Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = global::Android.Views.GravityFlags.Center
            };
            root.AddView(_bufferingIndicator, bufferingParams);

            // Setup overlay hide handler and runnable
            _overlayHandler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _overlayHideRunnable = new Java.Lang.Runnable(() => HideOverlayAnimated());

            _menuBackdrop = new global::Android.Views.View(this)
            {
                Visibility = global::Android.Views.ViewStates.Gone,
                Clickable = true
            };
            _menuBackdrop.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
            _menuBackdrop.Click += (_, __) =>
            {
                HideMenu();
                HideInfoOverlay();
            };
            root.AddView(_menuBackdrop, new FrameLayout.LayoutParams(
                global::Android.Views.ViewGroup.LayoutParams.MatchParent,
                global::Android.Views.ViewGroup.LayoutParams.MatchParent));

            _menuPanel = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                Visibility = global::Android.Views.ViewStates.Gone,
                Clickable = true,
                Focusable = true
            };
            _menuPanel.SetBackgroundDrawable(new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#CC101010")));
            _menuPanel.SetPadding((int)(12 * density), (int)(12 * density), (int)(12 * density), (int)(12 * density));

            var reportButton = new global::Android.Widget.Button(this) { Text = "Report stream" };
            _reportButton = reportButton;
            var videoInfoButton = new global::Android.Widget.Button(this) { Text = "Video Info" };
            _videoInfoButton = videoInfoButton;
            var inPlayScoresButton = new global::Android.Widget.Button(this) { Text = "Scores" };

            _reportStatusView = new TextView(this)
            {
                Text = "Reporting stream...",
                Visibility = global::Android.Views.ViewStates.Gone
            };
            _reportStatusView.SetTextColor(global::Android.Graphics.Color.ParseColor("#FFB300"));

            void ApplyTvMenuFocusStyling(global::Android.Widget.Button button)
            {
                if (!_isTvDevice) return;

                button.Focusable = true;
                button.FocusableInTouchMode = true;
                button.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#202020"));
                button.SetTextColor(global::Android.Graphics.Color.White);
                button.FocusChange += (_, args) =>
                {
                    if (args.HasFocus)
                    {
                        button.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#4A9EFF"));
                        button.SetTextColor(global::Android.Graphics.Color.Black);
                    }
                    else
                    {
                        button.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#202020"));
                        button.SetTextColor(global::Android.Graphics.Color.White);
                    }
                };
            }

            ApplyTvMenuFocusStyling(videoInfoButton);
            ApplyTvMenuFocusStyling(inPlayScoresButton);
            ApplyTvMenuFocusStyling(reportButton);

            _menuPanel.AddView(videoInfoButton);
            _menuPanel.AddView(inPlayScoresButton);
            _menuPanel.AddView(reportButton);
            _menuPanel.AddView(_reportStatusView);

            var menuPanelParams = new FrameLayout.LayoutParams(
                global::Android.Views.ViewGroup.LayoutParams.WrapContent,
                global::Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = global::Android.Views.GravityFlags.Bottom | global::Android.Views.GravityFlags.Left,
                LeftMargin = (int)(16 * density),
                BottomMargin = (int)(16 * density)
            };
            root.AddView(_menuPanel, menuPanelParams);

            _menuButton = new global::Android.Widget.ImageButton(this)
            {
                Visibility = _isTvDevice ? global::Android.Views.ViewStates.Gone : global::Android.Views.ViewStates.Visible
            };
            _menuButton.SetImageResource(global::Android.Resource.Drawable.IcMenuMore);
            _menuButton.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#AA000000"));
            _menuButton.Click += (_, __) =>
            {
                if (_isMenuVisible) HideMenu();
                else ShowMenu();
            };

            var menuButtonParams = new FrameLayout.LayoutParams((int)(44 * density), (int)(44 * density))
            {
                Gravity = global::Android.Views.GravityFlags.Bottom | global::Android.Views.GravityFlags.Left,
                LeftMargin = (int)(16 * density),
                BottomMargin = (int)(16 * density)
            };
            root.AddView(_menuButton, menuButtonParams);

            SetContentView(root);

            SubscribeToGamesSnapshot();

            if (!string.IsNullOrEmpty(_m3u8Url))
            {
                _logger?.LogInformation("[NativeVideoActivity] Starting initial playback: {Url}", _m3u8Url);
                SwitchToStreamUrl(_m3u8Url);
            }

            HideOverlayAnimated();
        }

        private static IStreamHealthReporter? ResolveHealthReporter()
        {
            try
            {
                return VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(IStreamHealthReporter)) as IStreamHealthReporter;
            }
            catch { return null; }
        }

        public void SwitchToStreamUrl(string m3u8Url)
        {
            try
            {
                RunOnUiThread(() =>
                {
                    try
                    {
                        if (_player == null)
                        {
                            _logger?.LogWarning("[NativeVideoActivity] Player null - cannot switch");
                            return;
                        }

                        _isPreparing = true;
                        _m3u8Url = m3u8Url;

                        var dataSourceFactory = new DefaultHttpDataSource.Factory();
                        // Include both Referer and User-Agent headers - many HLS hosts require both
                        try
                        {
                            // Use a custom data source factory that injects headers on every request
                            var headers = new System.Collections.Generic.Dictionary<string, string?>
                            {
                                ["Referer"] = _refererUrl ?? string.Empty,
                                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"
                            };
                            // Log the headers we will use to play the stream so we can verify requests in network traces
                            try { _logger?.LogInformation("[NativeVideoActivity] Playing stream. m3u8={Url} Referer={Referer} UserAgent={UA}", m3u8Url, headers.ContainsKey("Referer") ? headers["Referer"] : string.Empty, headers.ContainsKey("User-Agent") ? headers["User-Agent"] : string.Empty); } catch { }
                            var customFactory = new HeaderInjectingDataSourceFactory(headers);
                            var mediaSourceFactory = new HlsMediaSource.Factory(customFactory);
                            var mediaItem2 = new MediaItem.Builder().SetUri(m3u8Url).SetMimeType(MimeTypes.ApplicationM3u8).Build();
                            var mediaSource2 = mediaSourceFactory.CreateMediaSource(mediaItem2);
                            _player.SetMediaSource(mediaSource2);
                            _player.Prepare();
                            _player.PlayWhenReady = true;
                            _player.AddListener(new InternalPlayerListener(this));
                            _logger?.LogInformation("[NativeVideoActivity] Requested player to switch to {Url} (with header-injecting factory)", m3u8Url);
                            return;
                        }
                        catch
                        {
                            // Fallback to setting user agent only if default properties not available
                            try { dataSourceFactory.SetUserAgent("VardyParty/1.0"); } catch { }
                        }

                        // Log the fallback UA/referer when using the default factory
                        try { _logger?.LogInformation("[NativeVideoActivity] Playing stream (fallback factory). m3u8={Url} Referer={Referer} UserAgent={UA}", m3u8Url, _refererUrl ?? string.Empty, "VardyParty/1.0"); } catch { }
                        var mediaItem = new MediaItem.Builder().SetUri(m3u8Url).SetMimeType(MimeTypes.ApplicationM3u8).Build();
                        var mediaSource = new HlsMediaSource.Factory(dataSourceFactory).CreateMediaSource(mediaItem);

                        _player.SetMediaSource(mediaSource);
                        _player.Prepare();
                        _player.PlayWhenReady = true;

                        // Listen for state change to clear preparing flag
                        _player.AddListener(new InternalPlayerListener(this));

                        _logger?.LogInformation("[NativeVideoActivity] Requested player to switch to {Url}", m3u8Url);
                    }
                    catch (Exception ex)
                    {
                        _isPreparing = false;
                        _logger?.LogError(ex, "[NativeVideoActivity] SwitchToStreamUrl failed");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[NativeVideoActivity] SwitchToStreamUrl outer exception");
            }
        }

        private void SubscribeToGamesSnapshot()
        {
            try
            {
                var enriched = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(IEnrichedGameService)) as IEnrichedGameService;
                if (enriched == null)
                {
                    return;
                }

                _gamesSub = enriched.GamesStream.Subscribe(dict =>
                {
                    if (dict == null) return;
                    lock (_gamesLock)
                    {
                        _latestGamesByLeague = dict.ToDictionary(k => k.Key, v => v.Value?.ToList() ?? new List<Game>());
                    }

                    if (_isScoresTickerVisible)
                    {
                        RunOnUiThread(UpdateScoresTickerText);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Unable to subscribe to enriched games stream");
            }
        }

        private List<string> BuildSameLeagueInPlayScoreLines()
        {
            Dictionary<string, List<Game>>? snapshot;
            lock (_gamesLock)
            {
                snapshot = _latestGamesByLeague == null
                    ? null
                    : _latestGamesByLeague.ToDictionary(k => k.Key, v => v.Value.ToList());
            }

            if (snapshot == null || snapshot.Count == 0)
            {
                return new List<string>();
            }

            bool SameTeam(string left, string right) =>
                string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

            bool IsCurrentGame(Game game)
            {
                var home = game.DisplayHome;
                var away = game.DisplayAway;

                return (SameTeam(home, _currentHomeTeam) && SameTeam(away, _currentAwayTeam))
                    || (SameTeam(home, _currentAwayTeam) && SameTeam(away, _currentHomeTeam));
            }

            bool IsSameLeague(Game game)
            {
                if (string.IsNullOrWhiteSpace(_currentLeague)) return true;
                var league = game.DisplayLeague;
                return string.Equals(league?.Trim(), _currentLeague.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            bool IsInPlay(Game game)
            {
                if (game.IsFinished || game.IsPostponed) return false;
                return game.IsInProgress || game.IsHalfTime || game.Minute.HasValue;
            }

            string FormatScore(Game game)
            {
                var homeScore = game.HomeScore?.ToString() ?? "-";
                var awayScore = game.AwayScore?.ToString() ?? "-";
                return $"{homeScore}-{awayScore}";
            }

            string FormatStatus(Game game)
            {
                var status = game.DisplayStatusText();
                return string.IsNullOrWhiteSpace(status) ? "Live" : status;
            }

            return snapshot.Values
                .SelectMany(g => g)
                .Where(IsSameLeague)
                .Where(IsInPlay)
                .Where(g => !IsCurrentGame(g))
                .OrderByDescending(g => g.LiveMinuteForOrdering)
                .ThenBy(g => g.DisplayHome, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.DisplayHome} {FormatScore(g)} {g.DisplayAway} ({FormatStatus(g)})")
                .ToList();
        }

        private void ShowSameLeagueInPlayDialog()
        {
            try
            {
                var lines = BuildSameLeagueInPlayScoreLines();
                var title = string.IsNullOrWhiteSpace(_currentLeague)
                    ? "In-play games"
                    : $"In-play: {_currentLeague}";

                var message = lines.Count == 0
                    ? "No other in-play games in this league right now."
                    : string.Join("  •  ", lines);

                if (_scoresTickerText != null)
                {
                    _scoresTickerText.Text = $"{title}: {message}";
                    _scoresTickerText.Selected = true;
                    _scoresTickerText.RequestFocus();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to build same-league ticker text");
            }
        }

        private void UpdateScoresTickerText()
        {
            ShowSameLeagueInPlayDialog();
        }

        private void ToggleSameLeagueScoresTicker()
        {
            try
            {
                _isScoresTickerVisible = !_isScoresTickerVisible;
                if (_scoresTickerContainer != null)
                {
                    _scoresTickerContainer.Visibility = _isScoresTickerVisible
                        ? global::Android.Views.ViewStates.Visible
                        : global::Android.Views.ViewStates.Gone;
                }

                if (_isScoresTickerVisible)
                {
                    UpdateScoresTickerText();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to toggle same-league ticker");
            }
        }

        private void HideSystemUI()
        {
            try
            {
                var window = Window;
                if (window == null) return;

                // Hide status bar and navigation bar for immersive full-screen video
                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    // Android 11+ (API 30+) - Use WindowInsetsController
                    window.SetDecorFitsSystemWindows(false);
                    var controller = window.InsetsController;
                    if (controller != null)
                    {
                        controller.Hide(global::Android.Views.WindowInsets.Type.StatusBars() | global::Android.Views.WindowInsets.Type.NavigationBars());
                        controller.SystemBarsBehavior = (int)global::Android.Views.WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                    }
                }
                else
                {
                    // Android 10 and below - Use SystemUiVisibility flags
                    #pragma warning disable CS0618 // Type or member is obsolete
                    window.DecorView.SystemUiVisibility = (global::Android.Views.StatusBarVisibility)(
                        global::Android.Views.SystemUiFlags.Fullscreen |
                        global::Android.Views.SystemUiFlags.HideNavigation |
                        global::Android.Views.SystemUiFlags.Immersive |
                        global::Android.Views.SystemUiFlags.ImmersiveSticky);
                    #pragma warning restore CS0618
                }

                _logger?.LogInformation("[NativeVideoActivity] System UI hidden for full-screen video");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to hide system UI");
            }
        }
        private static string? BuildAspect(string? resolution)
        {
            if (string.IsNullOrEmpty(resolution)) return null;
            var parts = resolution.Split('x');
            if (parts.Length != 2) return null;
            if (!int.TryParse(parts[0], out var w)) return null;
            if (!int.TryParse(parts[1], out var h)) return null;
            int gcd(int a, int b) => b == 0 ? a : gcd(b, a % b);
            var g = gcd(w, h);
            return $"{w / g}:{h / g}";
        }

        private void UpdateOverlayText(VardyParty.Models.PlayerOverlayInfo? info)
        {
            if (info == null)
            {
                if (_titleView != null) _titleView.Text = string.Empty;
                if (_statusView != null) _statusView.Text = string.Empty;
                if (_indexView != null) _indexView.Text = string.Empty;
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
            catch { }

            var resolution = info.Resolution ?? string.Empty;
            var aspectRatio = info.AspectRatio ?? string.Empty;
            var br = info.BitrateKbps.HasValue ? $"{info.BitrateKbps} kbps" : string.Empty;
            var fr = info.FrameRate.HasValue ? $"{info.FrameRate:0.##} fps" : string.Empty;
            var buf = info.BufferPercent.HasValue ? $"Buffer {info.BufferPercent}%" : string.Empty;
            var m3u8 = info.M3u8Url ?? string.Empty;
            var referer = info.RefererUrl ?? string.Empty;
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
            string RefererHost(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return string.Empty;
                return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
            }
            if (!string.IsNullOrEmpty(m3u8)) m3u8 = $"Source: {StripQuery(m3u8)}";
            if (!string.IsNullOrEmpty(referer)) referer = $"Referer: {RefererHost(referer)}";
            // If video surface size is available, include resolution/framerate placeholder
            var resDetails = string.Empty;
            try
            {
                if (_videoWidth.HasValue && _videoHeight.HasValue)
                {
                    resDetails = $"{_videoWidth}x{_videoHeight}";
                }
            }
            catch { }

            // Show game title at top (prefer _gameTitle provided by route, fallback to stream title)
            if (_titleView != null) _titleView.Text = string.IsNullOrEmpty(_gameTitle) ? channel : _gameTitle;
            if (_statusView != null) _statusView.Text = statusLine;
            if (_indexView != null) _indexView.Text = indexLine;
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
            if (codecLineParts.Count > 0) lines.Add(string.Join(" • ", codecLineParts));

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

        private void ShowBufferingIndicator()
        {
            try
            {
                if (_bufferingIndicator == null) return;
                RunOnUiThread(() => _bufferingIndicator.Visibility = global::Android.Views.ViewStates.Visible);
            }
            catch { }
        }

        private void HideBufferingIndicator()
        {
            try
            {
                if (_bufferingIndicator == null) return;
                RunOnUiThread(() => _bufferingIndicator.Visibility = global::Android.Views.ViewStates.Gone);
            }
            catch { }
        }

        private void ShowOverlayAnimated()
        {
            try
            {
                if (_overlayContainer == null) return;
                RunOnUiThread(() =>
                {
                    _overlayContainer.Animate().Cancel();
                    _overlayContainer.Visibility = global::Android.Views.ViewStates.Visible;
                    _overlayContainer.Alpha = 0f;
                    _overlayContainer.Animate().Alpha(1f).SetDuration(200).Start();
                });
            }
            catch { }
        }

        private void HideOverlayAnimated()
        {
            try
            {
                if (_overlayContainer == null) return;
                RunOnUiThread(() =>
                {
                    _overlayContainer.Animate().Cancel();
                    _overlayContainer.Animate().Alpha(0f).SetDuration(300).WithEndAction(new Java.Lang.Runnable(() =>
                    {
                        try { _overlayContainer.Visibility = global::Android.Views.ViewStates.Gone; } catch { }
                    })).Start();
                });
            }
            catch { }
        }

        private void ScheduleHideOverlay()
        {
            try
            {
                if (_overlayLocked) return;
                _overlayHandler?.RemoveCallbacks(_overlayHideRunnable);
                _overlayHandler?.PostDelayed(_overlayHideRunnable, OverlayTimeoutMs);
            }
            catch { }
        }

        private PlaybackMetrics BuildPlaybackMetrics(bool isBuffering = false)
        {
            var metrics = new PlaybackMetrics
            {
                IsBuffering = isBuffering
            };

            try
            {
                var videoSize = _player?.VideoSize;
                if (videoSize != null && videoSize.Width > 0 && videoSize.Height > 0)
                {
                    metrics.Resolution = (videoSize.Width, videoSize.Height);
                    global::Android.Util.Log.Debug("VardyParty", $"[Android] Resolution: {videoSize.Width}x{videoSize.Height}");
                }
                else
                {
                    global::Android.Util.Log.Debug("VardyParty", $"[Android] Resolution not available (VideoSize: {videoSize?.Width}x{videoSize?.Height})");
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("VardyParty", $"[Android] Failed to extract resolution: {ex.Message}");
            }

            try
            {
                var format = _player?.VideoFormat;
                if (format != null && format.Bitrate > 0)
                {
                    metrics.BitrateKbps = format.Bitrate / 1000;
                    global::Android.Util.Log.Debug("VardyParty", $"[Android] Bitrate: {metrics.BitrateKbps} kbps");
                }
                else
                {
                    global::Android.Util.Log.Debug("VardyParty", $"[Android] Bitrate not available (Format bitrate: {format?.Bitrate ?? 0})");
                }

                // Extract codec information from video format
                if (format != null)
                {
                    metrics.VideoCodec = CodecMimeTypeToFriendlyName(format.SampleMimeType, isAudio: false);
                    if (format.FrameRate > 0)
                    {
                        metrics.Framerate = (int)format.FrameRate;
                        global::Android.Util.Log.Debug("VardyParty", $"[Android] Framerate: {metrics.Framerate} fps");
                    }
                    else
                    {
                        global::Android.Util.Log.Debug("VardyParty", $"[Android] Framerate not available (Format.FrameRate: {format.FrameRate})");
                    }
                    
                    if (!string.IsNullOrEmpty(metrics.VideoCodec))
                    {
                        global::Android.Util.Log.Debug("VardyParty", $"[Android] Video codec: {metrics.VideoCodec}");
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("VardyParty", $"[Android] Failed to extract video format: {ex.Message}");
            }

            // Extract audio codec from audio format if available
            try
            {
                var audioFormat = _player?.AudioFormat;
                if (audioFormat != null)
                {
                    metrics.AudioCodec = CodecMimeTypeToFriendlyName(audioFormat.SampleMimeType, isAudio: true);
                    if (!string.IsNullOrEmpty(metrics.AudioCodec))
                    {
                        global::Android.Util.Log.Debug("VardyParty", $"[Android] Audio codec: {metrics.AudioCodec}");
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("VardyParty", $"[Android] Failed to extract audio format: {ex.Message}");
            }

            // Update the service with extracted metrics
            try
            {
                VardyParty.Platforms.Android.AndroidVideoPlayerService.UpdateMetrics(metrics);
            }
            catch { }

            return metrics;
        }

        private void StartHealthReporting()
        {
            if (_healthReporter == null) return;
            _healthReportTimer?.Dispose();
            _healthReportTimer = new Timer(async _ =>
            {
                try
                {
                    var metrics = BuildPlaybackMetrics();
                    _metricsWindow.ResetIfExpired();
                    if (_metricsWindow.BufferingEvents > 0)
                    {
                        metrics.IsBuffering = true;
                    }
                    if (metrics.BitrateKbps.HasValue)
                    {
                        _metricsWindow.AddBitrate(metrics.BitrateKbps.Value);
                    }

                    await _healthReporter.ReportPlaybackMetricsAsync(_m3u8Url, _refererUrl, metrics);
                }
                catch { }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void ReportPlaybackStarted()
        {
            if (_healthReporter == null) return;
            try
            {
                var metrics = BuildPlaybackMetrics();
                if (metrics.BitrateKbps.HasValue)
                {
                    _metricsWindow.AddBitrate(metrics.BitrateKbps.Value);
                }

                _ = _healthReporter.ReportPlaybackStartedAsync(_m3u8Url, _refererUrl, metrics);
            }
            catch { }
        }

        private void ReportBuffering()
        {
            if (_healthReporter == null) return;
            try
            {
                var metrics = BuildPlaybackMetrics(isBuffering: true);
                _metricsWindow.AddBufferingEvent();
                if (metrics.BitrateKbps.HasValue)
                {
                    _metricsWindow.AddBitrate(metrics.BitrateKbps.Value);
                }

                _ = _healthReporter.ReportBufferingAsync(_m3u8Url, _refererUrl, metrics);
            }
            catch { }
        }

        private void ReportPlaybackError(string? error)
        {
            if (_healthReporter == null) return;
            try
            {
                _metricsWindow.AddError();
                _ = _healthReporter.ReportPlaybackErrorAsync(_m3u8Url, _refererUrl, error);
            }
            catch { }
        }

        private static string? CodecMimeTypeToFriendlyName(string? mimeType, bool isAudio)
        {
            if (string.IsNullOrEmpty(mimeType))
            {
                return null;
            }

            var lower = mimeType.ToLowerInvariant();
            
            // Extract codec part from MIME type (e.g., "video/avc" -> "avc", "audio/mp4a-latm" -> "mp4a-latm")
            if (lower.Contains("/"))
            {
                var codec = lower.Split('/')[1];
                
                // Video codecs
                if (!isAudio)
                {
                    return codec switch
                    {
                        "avc" or "avc1" or "avc3" => "H.264",
                        "hevc" or "hev1" or "hvc1" => "H.265",
                        "vp9" => "VP9",
                        "vp8" => "VP8",
                        "av1" => "AV1",
                        "mpeg2" => "MPEG-2",
                        _ => codec
                    };
                }
                else
                {
                    // Audio codecs
                    return codec switch
                    {
                        "mp4a" or "mp4a-latm" => "AAC",
                        "ac-3" => "AC-3",
                        "ec-3" => "E-AC-3",
                        "opus" => "Opus",
                        "vorbis" => "Vorbis",
                        "flac" => "FLAC",
                        "mp3" or "mpeg" => "MP3",
                        "pcm" => "PCM",
                        _ => codec
                    };
                }
            }

            return mimeType;
        }

        private void EvaluateHealthAndSwitchIfNeeded()
        {
            try
            {
                if (_metricsWindow.IsHealthDeclined())
                {
                    VardyParty.Platforms.Android.AndroidVideoPlayerService.RequestNextStream().ContinueWith(_ => { });
                }
            }
            catch { }
        }

        private void ShowMenu()
        {
            _isMenuVisible = true;
            if (_menuPanel != null) _menuPanel.Visibility = global::Android.Views.ViewStates.Visible;
            _videoInfoButton?.Post(() => _videoInfoButton.RequestFocus());
            UpdateBackdropVisibility();
        }

        private void HideMenu()
        {
            _isMenuVisible = false;
            if (_menuPanel != null) _menuPanel.Visibility = global::Android.Views.ViewStates.Gone;
            UpdateBackdropVisibility();
        }

        private void ShowInfoOverlay()
        {
            _isInfoVisible = true;
            _overlayLocked = true;
            ShowOverlayAnimated();
            UpdateBackdropVisibility();
        }

        private void HideInfoOverlay()
        {
            _isInfoVisible = false;
            _overlayLocked = false;
            HideOverlayAnimated();
            UpdateBackdropVisibility();
        }

        private void UpdateBackdropVisibility()
        {
            if (_menuBackdrop == null) return;
            _menuBackdrop.Visibility = (_isMenuVisible || _isInfoVisible) && !_isTvDevice
                ? global::Android.Views.ViewStates.Visible
                : global::Android.Views.ViewStates.Gone;
        }

        public override bool OnKeyDown(global::Android.Views.Keycode keyCode, global::Android.Views.KeyEvent e)
        {
            try
            {
                switch (keyCode)
                {
                    case global::Android.Views.Keycode.DpadCenter:
                    case global::Android.Views.Keycode.Enter:
                        if (_isTvDevice)
                        {
                            if (_isMenuVisible)
                            {
                                HideMenu();
                                return true;
                            }

                            if (_isInfoVisible)
                            {
                                HideInfoOverlay();
                                return true;
                            }

                            ShowMenu();
                            return true;
                        }

                        if (_isInfoVisible)
                        {
                            HideInfoOverlay();
                        }
                        else
                        {
                            ShowInfoOverlay();
                        }
                        return true;

                    case global::Android.Views.Keycode.DpadRight:
                        try
                        {
                            bool wasVisible = _overlayContainer != null && _overlayContainer.Visibility == global::Android.Views.ViewStates.Visible;
                            if (!wasVisible)
                            {
                                _suppressOverlayShow = true;
                            }

                            if (_switching != null)
                            {
                                var requestTask = VardyParty.Platforms.Android.AndroidVideoPlayerService.RequestNextStream();
                                if (requestTask != null)
                                {
                                    requestTask.ContinueWith(_ => { }, TaskScheduler.Default);
                                }
                                else
                                {
                                    _switching.SwitchToNextStream();
                                }
                            }

                            try { Toast.MakeText(this, "Switch requested...", ToastLength.Short)?.Show(); } catch { }

                            if (wasVisible)
                            {
                                ShowOverlayAnimated();
                                if (!_overlayLocked) ScheduleHideOverlay();
                            }
                        }
                        catch { }
                        return true;

                    case global::Android.Views.Keycode.Back:
                        if (_isMenuVisible)
                        {
                            HideMenu();
                            return true;
                        }

                        if (_isInfoVisible)
                        {
                            HideInfoOverlay();
                            return true;
                        }

                        if (_isScoresTickerVisible)
                        {
                            ToggleSameLeagueScoresTicker();
                            return true;
                        }

                        try { _switching?.Cleanup(); } catch { }
                        try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "User closed video player" }); } catch { }
                        Finish();
                        return true;
                }
            }
            catch { }

            return base.OnKeyDown(keyCode, e);
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            
            // Re-hide system UI when window regains focus (e.g., after swiping down status bar)
            if (hasFocus)
            {
                HideSystemUI();
            }
        }

        private void SetupPinchZoom(FrameLayout container, PlayerView playerView)
        {
            try
            {
                // Set PlayerView to fit mode so video maintains aspect ratio
                playerView.ResizeMode = AspectRatioFrameLayout.ResizeModeFit;
                
                // Create gesture detectors for both pinch-zoom and drag-pan
                var scaleGestureDetector = new global::Android.Views.ScaleGestureDetector(this, new PinchZoomListener(container));
                var gestureDetector = new global::Android.Views.GestureDetector(this, new DragGestureListener(container));
                
                // Attach combined touch listener to the container
                container.SetOnTouchListener(new ZoomAndDragTouchListener(scaleGestureDetector, gestureDetector, container));
                
                _logger?.LogInformation("[NativeVideoActivity] Pinch-to-zoom and drag enabled for Lubo!");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to setup pinch-zoom");
            }
        }

        private class ZoomAndDragTouchListener : Java.Lang.Object, global::Android.Views.View.IOnTouchListener
        {
            private readonly global::Android.Views.ScaleGestureDetector _scaleDetector;
            private readonly global::Android.Views.GestureDetector _gestureDetector;
            private readonly FrameLayout _container;

            public ZoomAndDragTouchListener(
                global::Android.Views.ScaleGestureDetector scaleDetector, 
                global::Android.Views.GestureDetector gestureDetector,
                FrameLayout container)
            {
                _scaleDetector = scaleDetector;
                _gestureDetector = gestureDetector;
                _container = container;
            }

            public bool OnTouch(global::Android.Views.View? v, global::Android.Views.MotionEvent? e)
            {
                // Handle scale gestures first (pinch-to-zoom)
                var scaleHandled = _scaleDetector.OnTouchEvent(e);
                
                // Handle drag gestures when not scaling (single finger pan)
                var dragHandled = _gestureDetector.OnTouchEvent(e);
                
                // Consume event if either gesture was handled
                return scaleHandled || dragHandled;
            }
        }

        private class DragGestureListener : global::Android.Views.GestureDetector.SimpleOnGestureListener
        {
            private readonly FrameLayout _container;
            private float _translationX = 0f;
            private float _translationY = 0f;

            public DragGestureListener(FrameLayout container)
            {
                _container = container;
            }

            public override bool OnScroll(global::Android.Views.MotionEvent? e1, global::Android.Views.MotionEvent? e2, float distanceX, float distanceY)
            {
                // Only allow dragging when zoomed in (scale > 1)
                if (_container.ScaleX <= 1.0f) return false;

                // Calculate new translation
                _translationX -= distanceX;
                _translationY -= distanceY;

                // Calculate boundaries based on current scale
                var scale = _container.ScaleX;
                var maxTranslationX = (_container.Width * (scale - 1f)) / 2f;
                var maxTranslationY = (_container.Height * (scale - 1f)) / 2f;

                // Clamp translation to keep video on screen
                _translationX = Math.Max(-maxTranslationX, Math.Min(_translationX, maxTranslationX));
                _translationY = Math.Max(-maxTranslationY, Math.Min(_translationY, maxTranslationY));

                // Apply translation
                _container.TranslationX = _translationX;
                _container.TranslationY = _translationY;

                global::Android.Util.Log.Debug("VardyParty", $"[Drag] Translation: ({_translationX:F0}, {_translationY:F0})");
                
                return true;
            }
        }

        private class PinchZoomListener : global::Android.Views.ScaleGestureDetector.SimpleOnScaleGestureListener
        {
            private readonly FrameLayout _container;
            private float _scaleFactor = 1.0f;
            private const float MinScale = 1.0f;
            private const float MaxScale = 4.0f;

            public PinchZoomListener(FrameLayout container)
            {
                _container = container;
            }

            public override bool OnScaleBegin(global::Android.Views.ScaleGestureDetector? detector)
            {
                global::Android.Util.Log.Debug("VardyParty", "[Zoom] Pinch gesture started");
                return true;
            }

            public override bool OnScale(global::Android.Views.ScaleGestureDetector? detector)
            {
                if (detector == null) return false;

                float previousScale = _scaleFactor;
                _scaleFactor *= detector.ScaleFactor;
                _scaleFactor = Math.Max(MinScale, Math.Min(_scaleFactor, MaxScale));

                // Only apply if scale actually changed
                if (Math.Abs(previousScale - _scaleFactor) > 0.01f)
                {
                    // Set pivot to gesture focus point for natural zoom at pinch center
                    _container.PivotX = detector.FocusX;
                    _container.PivotY = detector.FocusY;
                    
                    // Apply scale to container
                    _container.ScaleX = _scaleFactor;
                    _container.ScaleY = _scaleFactor;

                    global::Android.Util.Log.Debug("VardyParty", $"[Zoom] Scale: {_scaleFactor:F2}x pivot: ({detector.FocusX:F0}, {detector.FocusY:F0})");
                    
                    // Reset translation when zooming out to 1x
                    if (_scaleFactor <= 1.01f)
                    {
                        _container.TranslationX = 0f;
                        _container.TranslationY = 0f;
                        global::Android.Util.Log.Debug("VardyParty", "[Zoom] Reset to 1x - cleared translation");
                    }
                }
                
                return true;
            }

            public override void OnScaleEnd(global::Android.Views.ScaleGestureDetector? detector)
            {
                global::Android.Util.Log.Info("VardyParty", $"[Zoom] Pinch complete. Final scale: {_scaleFactor:F2}x");
            }
        }

        private class InternalPlayerListener : Java.Lang.Object, IPlayerListener
        {
            private readonly NativeVideoActivity _activity;
            private bool _metadataReported = false;
            
            public InternalPlayerListener(NativeVideoActivity activity) => _activity = activity;

            public void OnPlaybackStateChanged(int playbackState)
            {
                try
                {
                    _activity._playbackStateText = playbackState switch
                    {
                        PlayerStateIdle => VardyParty.Resources.Strings.Resources.StatusIdle,
                        PlayerStateBuffering => VardyParty.Resources.Strings.Resources.StatusBuffering,
                        PlayerStateReady => VardyParty.Resources.Strings.Resources.StatusPlaying,
                        PlayerStateEnded => VardyParty.Resources.Strings.Resources.StatusEnded,
                        _ => VardyParty.Resources.Strings.Resources.StatusPlaying
                    };
                }
                catch { }

                // STATE_READY = 3
                if (playbackState == PlayerStateReady)
                {
                    _activity._isBuffering = false;
                    _activity.HideBufferingIndicator();
                    _activity._isPreparing = false;
                    _activity._logger?.LogInformation("[NativeVideoActivity] Player ready");
                    // Don't report yet - wait for OnTracksChanged to extract real metadata
                    _activity.StartHealthReporting();
                    // Update overlay UI now that player is ready
                    var current = _activity._switching?.GetCurrentStream();
                    if (current != null)
                    {
                        var info = new VardyParty.Models.PlayerOverlayInfo
                        {
                            Index = _activity._switching.GetCurrentStreamIndex(),
                            Total = _activity._switching.GetHealthyStreams().Count,
                            Channel = current.Stream?.Channel,
                            BitrateKbps = current.Stream?.BitrateKbps ?? current.Health?.Bitrate,
                            Resolution = current.Stream?.Resolution ?? current.Health?.Resolution,
                            M3u8Url = current.ResolvedM3U8Url ?? _activity._m3u8Url,
                            RefererUrl = _activity._refererUrl,
                            BufferPercent = _activity._player?.BufferedPercentage,
                            FrameRate = current.Health?.FrameRate != null ? (double?)current.Health.FrameRate : null,
                            VideoCodec = MapCodecToFriendlyName(current.Health?.VideoCodec),
                            AudioCodec = MapCodecToFriendlyName(current.Health?.AudioCodec),
                            AspectRatio = BuildAspect(current.Stream?.Resolution ?? current.Health?.Resolution),
                            Title = current.Stream?.Channel
                        };
                        VardyParty.Platforms.Android.AndroidVideoPlayerService.SetOverlayInfo(info);
                        // capture video size from player if available
                        try
                        {
                            var videoSize = _activity._player?.VideoSize;
                            if (videoSize != null)
                            {
                                _activity._videoWidth = videoSize.Width;
                                _activity._videoHeight = videoSize.Height;
                            }
                        }
                        catch { }
                        _activity.RunOnUiThread(() => _activity.UpdateOverlayText(info));
                    }
                }
                else
                {
                    try
                    {
                        var last = _activity._lastOverlayInfo;
                        if (last != null)
                        {
                            _activity.RunOnUiThread(() => _activity.UpdateOverlayText(last));
                        }
                    }
                    catch { }
                }

                if (playbackState == PlayerStateEnded)
                {
                    _activity._logger?.LogInformation("[NativeVideoActivity] Playback ended");
                    _metadataReported = false;
                    // Playback ended - free in-memory manifest entry for current URL to reduce memory
                    try
                    {
                        var svc = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(VardyParty.Services.IStreamSwitchingService)) as VardyParty.Services.IStreamSwitchingService;
                        try
                        {
                            try
                            {
                                if (!string.IsNullOrEmpty(_activity._m3u8Url) && _activity._inMemoryManifestMap != null)
                                {
                                    _activity._inMemoryManifestMap.TryRemove(_activity._m3u8Url, out _);
                                    global::Android.Util.Log.Info("VardyParty", $"[NativeVideoActivity] Cleared in-memory manifest for {_activity._m3u8Url}");
                                }
                            }
                            catch { }
                        }
                        catch { }
                    }
                    catch { }
                }
            }

            public void OnTracksChanged(Tracks? tracks)
            {
                // OnTracksChanged is called when the player has parsed the stream tracks and codecs
                // This is when we have REAL metadata, not manifest predictions
                if (!_metadataReported && tracks != null && _activity._player != null)
                {
                    _metadataReported = true;
                    _activity._logger?.LogInformation("[NativeVideoActivity] Tracks changed - reporting playback started with metadata");
                    
                    // Now extract real metadata from the player and report
                    _activity.ReportPlaybackStarted();
                }
            }

            public void OnVideoSizeChanged(VideoSize? videoSize) { }
            public void OnIsLoadingChanged(bool isLoading)
            {
                try
                {
                    if (isLoading)
                    {
                        _activity._playbackStateText = VardyParty.Resources.Strings.Resources.StatusBuffering;
                        _activity._isBuffering = true;
                        _activity.ShowBufferingIndicator();
                        _activity.HideOverlayAnimated();
                        _activity.ReportBuffering();
                        _activity.EvaluateHealthAndSwitchIfNeeded();
                    }
                    else if (_activity._player?.PlaybackState == PlayerStateReady)
                    {
                        _activity._playbackStateText = VardyParty.Resources.Strings.Resources.StatusPlaying;
                        _activity._isBuffering = false;
                        _activity.HideBufferingIndicator();
                    }

                    var last = _activity._lastOverlayInfo;
                    if (last != null)
                    {
                        _activity.RunOnUiThread(() => _activity.UpdateOverlayText(last));
                    }
                }
                catch { }
            }
            public void OnAudioAttributesChanged(AndroidX.Media3.Common.AudioAttributes? attributes) { }
            public void OnAudioSessionIdChanged(int audioSessionId) { }
            public void OnAvailableCommandsChanged(AndroidX.Media3.Common.PlayerCommands? availableCommands) { }
            public void OnCues(AndroidX.Media3.Common.Text.CueGroup? cueGroup) { }
            public void OnCues(IList<AndroidX.Media3.Common.Text.Cue>? cues) { }
            public void OnDeviceInfoChanged(AndroidX.Media3.Common.DeviceInfo? deviceInfo) { }
            public void OnIsPlayingChanged(bool isPlaying) { }
            public void OnMediaItemTransition(MediaItem? mediaItem, int reason) { }
            public void OnMediaMetadataChanged(AndroidX.Media3.Common.MediaMetadata? mediaMetadata) { }
            public void OnMetadata(Metadata? metadata) { }
            public void OnPlayWhenReadyChanged(bool playWhenReady, int reason) { }
            public void OnPlaybackParametersChanged(PlaybackParameters? playbackParameters) { }
            public void OnPlaybackSuppressionReasonChanged(int playbackSuppressionReason) { }
            public void OnPlayerErrorChanged(PlaybackException? error) { }
            public void OnPlaylistMetadataChanged(AndroidX.Media3.Common.MediaMetadata? mediaMetadata) { }
            public void OnPositionDiscontinuity(AndroidX.Media3.Common.PlayerPositionInfo? oldPosition, AndroidX.Media3.Common.PlayerPositionInfo? newPosition, int reason) { }
            public void OnRenderedFirstFrame() { }
            public void OnRepeatModeChanged(int repeatMode) { }
            public void OnSeekBackIncrementChanged(long seekBackIncrementMs) { }
            public void OnSeekForwardIncrementChanged(long seekForwardIncrementMs) { }
            public void OnShuffleModeEnabledChanged(bool shuffleModeEnabled) { }
            public void OnSkipSilenceEnabledChanged(bool skipSilenceEnabled) { }
            public void OnSurfaceSizeChanged(int width, int height) { }
            public void OnTimelineChanged(Timeline? timeline, int reason) { }
            public void OnVolumeChanged(float volume) { }
        }
    }
}
#endif
