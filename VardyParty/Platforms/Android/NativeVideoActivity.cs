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
using VardyParty.Orchestrators;
using VardyParty.Playback;
using Microsoft.Extensions.Logging;
using System.Threading;
using VardyParty.Health;

namespace VardyParty.Platforms.Android
{
    [Activity(Label = "Video Player", Theme = "@style/Maui.MainTheme", MainLauncher = false, ScreenOrientation = global::Android.Content.PM.ScreenOrientation.Landscape)]
    public partial class NativeVideoActivity : Activity
    {
        // Constructor DI - OS will call default ctor which chains to parameterized ctor
        public NativeVideoActivity() : this(ResolveSwitching(), ResolveLogger()) { }

        public NativeVideoActivity(IStreamSwitchingService? switching, ILogger<NativeVideoActivity>? logger, IStreamHealthReporter? healthReporter = null)
        {
            _switching = switching;
            _logger = logger;
            _healthReporter = healthReporter ?? ResolveHealthReporter();
            _engine.EngineEvent += (_, engineEvent) => DispatchEngine(engineEvent);
            _engine.AttachHandler = (url, _, _) =>
            {
                AttachEngine(url);
                return Task.CompletedTask;
            };
            _engine.StopHandler = _ =>
            {
                StopAndReleasePlayer(release: false);
                return Task.CompletedTask;
            };
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
            catch
            {
                return codec;
            }
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
                    try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Manifest fallback failed" }); }
                    catch (Exception ex) { LogIgnored("ReportPlaybackResult", ex); }
                    return;
                }

                var content = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger?.LogWarning("[NativeVideoActivity] Manifest fallback empty content");
                    try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Manifest empty" }); }
                    catch (Exception ex) { LogIgnored("ReportPlaybackResult", ex); }
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
                            var mediaBuilder = new MediaItem.Builder();
                            mediaBuilder.SetUri(m3u8Url);
                            mediaBuilder.SetMimeType(MimeTypes.ApplicationM3u8);
                            var mediaItem = mediaBuilder.Build()
                                ?? throw new InvalidOperationException("MediaItem.Build returned null.");
                            var mediaSource = new HlsMediaSource.Factory(interceptFactory).CreateMediaSource(mediaItem)
                                ?? throw new InvalidOperationException("CreateMediaSource returned null.");
                            _player?.SetMediaSource(mediaSource);
                            _player?.Prepare();
                            _player?.Play();
                            _logger?.LogInformation("[NativeVideoActivity] Serving manifest from memory for {Url}", m3u8Url);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "[NativeVideoActivity] Fallback play failed (in-memory)");
                            try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Fallback play failed" }); }
                            catch (Exception innerEx) { LogIgnored("ReportPlaybackResult", innerEx); }
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[NativeVideoActivity] Manifest fallback in-memory exception");
                    try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Manifest fallback exception" }); }
                    catch (Exception innerEx) { LogIgnored("ReportPlaybackResult", innerEx); }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Manifest fallback exception");
                try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = "Manifest fallback exception" }); }
                catch (Exception reportEx) { LogIgnored("ReportPlaybackResult", reportEx); }
            }
        }

        // Support post-construction injection for true constructor-like DI via activity factory / lifecycle hook
        public void InjectServices(
            IStreamSwitchingService? switching,
            ILogger<NativeVideoActivity>? logger,
            IStreamHealthReporter? healthReporter = null,
            IEnrichedGameService? enrichedGames = null,
            IApiService? api = null,
            IStreamResolutionOrchestrator? orchestrator = null)
        {
            if (switching != null) _switching = switching;
            if (logger != null) _logger = logger;
            if (healthReporter != null) _healthReporter = healthReporter;
            if (enrichedGames != null) _enrichedGames = enrichedGames;
            if (api != null) _api = api;
            if (orchestrator != null) _orchestrator = orchestrator;
        }

        private void LogIgnored(string operation, Exception ex)
            => _logger?.LogDebug(ex, "[NativeVideoActivity] {Operation} failed", operation);

        // Test helpers / query methods to allow unit testing decision logic without running Android UI
        public void SetPreparingForTests(bool preparing) => _isPreparing = preparing;
        public void SetCurrentPlaybackUrlForTests(string? url) => _m3u8Url = url ?? string.Empty;
        public bool CanSwitchTo(string candidateUrl)
            => PlaybackPolicy.CanAttach(_m3u8Url, candidateUrl, _isPreparing);

        private static IStreamSwitchingService? ResolveSwitching()
        {
            try
            {
                return VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(IStreamSwitchingService)) as IStreamSwitchingService;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeVideoActivity] ResolveSwitching failed: {ex.Message}");
                return null;
            }
        }

        private static ILogger<NativeVideoActivity>? ResolveLogger()
        {
            try
            {
                var lf = VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
                return lf?.CreateLogger<NativeVideoActivity>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeVideoActivity] ResolveLogger failed: {ex.Message}");
                return null;
            }
        }

        private static IStreamHealthReporter? ResolveHealthReporter()
        {
            try
            {
                return VardyParty.AppServiceProvider.ServiceProvider?.GetService(typeof(IStreamHealthReporter)) as IStreamHealthReporter;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeVideoActivity] ResolveHealthReporter failed: {ex.Message}");
                return null;
            }
        }

        private IExoPlayer? _player;
        private InternalPlayerListener? _playerListener;
        private PlayerView? _playerView;
        private TextView? _titleView;
        private TextView? _statusView;
        private TextView? _indexView;
        private TextView? _sourceBadgeView;
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
        private IEnrichedGameService? _enrichedGames;
        private IApiService? _api;
        private IStreamResolutionOrchestrator? _orchestrator;
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
        private LinearLayout? _tickerInner;
        private TextView? _tickerText1;
        private TextView? _tickerText2;
        private global::Android.OS.Handler? _tickerHandler;
        private Java.Lang.Runnable? _tickerRunnable;
        private float _tickerScrollX;
        private float _tickerPixelsPerFrame = 2f; // scroll speed
        private bool _isScoresTickerVisible;
        private ScoresTickerMode _scoresTickerMode = ScoresTickerMode.SameLeagueInPlay;
        private bool _isMenuVisible;
        private bool _isInfoVisible;
        private bool _isTvDevice;
        private TextView? _streamToastView;
        private global::Android.OS.Handler? _streamToastHandler;
        private Java.Lang.IRunnable? _streamToastRunnable;
        private int _lastToastIndex = -1;
        private int _lastToastTotal = -1;
        private bool _playbackResultReported;
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
            try { AndroidActivityFactory.Inject(this); }
            catch (Exception ex) { LogIgnored("AndroidActivityFactory.Inject", ex); }

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
            _playerListener = new InternalPlayerListener(this);
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
            var resources = Resources ?? throw new InvalidOperationException("Resources are unavailable.");
            var metrics = resources.DisplayMetrics ?? throw new InvalidOperationException("Display metrics are unavailable.");
            float density = metrics.Density; // dp scaling
            float scaledDensity = density * (resources.Configuration?.FontScale ?? 1f); // sp scaling for fonts

            // Use conservative base sizes and avoid upscaling on TV so overlay fits
            float fontScaleCap = Math.Min(1.0f, scaledDensity); // do not upscale
            float titleSp = 16f * fontScaleCap;
            float bodySp = 12f * fontScaleCap;
            float smallSp = 10f * fontScaleCap;

            int paddingDp = (int)(8 * density);
            int outerRightDp = (int)(20 * density);
            int outerTopDp = (int)(20 * density);

            // Title will show the game (Home vs Away) at top
            _titleView = new TextView(this) { Text = string.Empty };
            _titleView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, titleSp);
            _titleView.SetTypeface(global::Android.Graphics.Typeface.DefaultBold, global::Android.Graphics.TypefaceStyle.Bold);
            _titleView.SetTextColor(global::Android.Graphics.Color.White);
            ConfigureEmojiFriendlyTextView(_titleView);

            // Reduced body fonts for tighter layout
            _statusView = new TextView(this);
            // Use consistent body font for status/index/quality lines
            _statusView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bodySp);
            _statusView.SetTextColor(global::Android.Graphics.Color.White);

            _indexView = new TextView(this);
            _indexView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bodySp);
            _indexView.SetTextColor(global::Android.Graphics.Color.White);

            _sourceBadgeView = new TextView(this)
            {
                Visibility = global::Android.Views.ViewStates.Gone
            };
            _sourceBadgeView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, smallSp);
            _sourceBadgeView.SetTypeface(global::Android.Graphics.Typeface.DefaultBold, global::Android.Graphics.TypefaceStyle.Bold);
            _sourceBadgeView.SetPadding((int)(6 * density), (int)(1 * density), (int)(6 * density), (int)(1 * density));

            _qualityView = new TextView(this);
            _qualityView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bodySp);
            _qualityView.SetTextColor(global::Android.Graphics.Color.White);

            _resBrView = new TextView(this);
            // Stream detail (resolution/bitrate/codecs/urls) should use a smaller font
            _resBrView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, smallSp);
            _resBrView.SetTextColor(global::Android.Graphics.Color.LightGray);
            _resBrView.SetMaxLines(8);
            _resBrView.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;

            var linear = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical
            };
            // store overlay container to control visibility/animations
            _overlayContainer = linear;
            linear.Background = new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#99000000"));
            linear.AddView(_titleView);
            linear.AddView(_statusView);
            linear.AddView(_indexView);
            linear.AddView(_sourceBadgeView);
            linear.AddView(_qualityView);
            linear.AddView(_resBrView);
            linear.Alpha = 0.95f;
            linear.SetPadding(paddingDp, paddingDp, paddingDp, paddingDp);

            // Cap overlay at half the screen width so long source URLs don't push it across the screen.
            int overlayMaxWidth = metrics.WidthPixels / 2;
            var overlayParams = new FrameLayout.LayoutParams(overlayMaxWidth, global::Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                // Top-right keeps broadcaster score bugs (usually top-left) visible on TV feeds.
                Gravity = global::Android.Views.GravityFlags.Top | global::Android.Views.GravityFlags.Right,
                RightMargin = outerRightDp,
                TopMargin = outerTopDp
            };
            root.AddView(linear, overlayParams);

            // Stream toast — top-right, just below the detail overlay.
            // Shows "Stream: x/y" briefly when a new healthy stream is found and the info overlay is hidden.
            _streamToastView = new TextView(this)
            {
                Visibility = global::Android.Views.ViewStates.Gone
            };
            _streamToastView.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bodySp);
            _streamToastView.SetTextColor(global::Android.Graphics.Color.White);
            _streamToastView.SetTypeface(global::Android.Graphics.Typeface.DefaultBold, global::Android.Graphics.TypefaceStyle.Bold);
            _streamToastView.Background = new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#99000000"));
            _streamToastView.SetPadding(paddingDp, (int)(4 * density), paddingDp, (int)(4 * density));
            var streamToastParams = new FrameLayout.LayoutParams(
                global::Android.Views.ViewGroup.LayoutParams.WrapContent,
                global::Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = global::Android.Views.GravityFlags.Top | global::Android.Views.GravityFlags.Right,
                RightMargin = outerRightDp,
                TopMargin = outerTopDp + (int)(80 * density)
            };
            root.AddView(_streamToastView, streamToastParams);
            _streamToastHandler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _streamToastRunnable = new Java.Lang.Runnable(() =>
            {
                try { _streamToastView.Visibility = global::Android.Views.ViewStates.Gone; } catch { }
            });

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
            _menuPanel.Background = new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#CC101010"));
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

            videoInfoButton.Click += (_, __) =>
            {
                HideMenu();
                ShowInfoOverlay();
            };

            inPlayScoresButton.Click += (_, __) =>
            {
                HideMenu();
                ToggleSameLeagueScoresTicker();
            };

            reportButton.Click += async (_, __) =>
            {
                if (_reportStatusView != null)
                {
                    _reportStatusView.Visibility = global::Android.Views.ViewStates.Visible;
                    _reportStatusView.Text = "Reporting stream...";
                }

                try
                {
                    if (_orchestrator != null)
                    {
                        await _orchestrator.ReportCurrentStreamAsBadAsync("User reported bad stream");
                        if (_reportStatusView != null) _reportStatusView.Text = "Stream reported";
                    }
                    else if (_reportStatusView != null)
                    {
                        _reportStatusView.Text = "Report unavailable";
                    }
                }
                catch (Exception ex)
                {
                    LogIgnored("ReportCurrentStreamAsBad", ex);
                    if (_reportStatusView != null) _reportStatusView.Text = "Report failed";
                }

                try { await Task.Delay(900); } catch (Exception ex) { LogIgnored("ReportStatusDelay", ex); }
                if (_reportStatusView != null) _reportStatusView.Visibility = global::Android.Views.ViewStates.Gone;
                HideMenu();
            };

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
                BottomMargin = (int)((16 + 44 + 8) * density)
            };
            root.AddView(_menuPanel, menuPanelParams);

            _scoresTickerContainer = new LinearLayout(this)
            {
                Orientation = Orientation.Horizontal,
                Visibility = global::Android.Views.ViewStates.Gone
            };
            _scoresTickerContainer.Background = new global::Android.Graphics.Drawables.ColorDrawable(global::Android.Graphics.Color.ParseColor("#CC101010"));
            _scoresTickerContainer.SetPadding((int)(12 * density), (int)(8 * density), (int)(12 * density), (int)(8 * density));

            // Seamless infinite ticker: two identical TextViews side-by-side inside a
            // clipped inner LinearLayout, translated continuously by a Runnable loop.
            // When the first copy scrolls fully off the left, reset to 0 — seamless wrap.
            _tickerInner = new LinearLayout(this) { Orientation = Orientation.Horizontal };

            TextView MakeTickerTextView()
            {
                var tv = new TextView(this) { Text = string.Empty };
                tv.SetTextColor(global::Android.Graphics.Color.White);
                tv.SetTextSize(global::Android.Util.ComplexUnitType.Sp, bodySp);
                ConfigureEmojiFriendlyTextView(tv);
                tv.SetSingleLine(true);
                // Do NOT call SetHorizontallyScrolling — that is for TextView's own marquee
                // engine and interferes with the manual TranslationX scroll approach.
                tv.SetPadding(0, 0, (int)(64 * density), 0); // gap between copies
                return tv;
            }

            _tickerText1 = MakeTickerTextView();
            _tickerText2 = MakeTickerTextView();
            // WrapContent so each TextView measures at its natural text width, not screen width.
            _tickerInner.AddView(_tickerText1, new LinearLayout.LayoutParams(
                global::Android.Views.ViewGroup.LayoutParams.WrapContent,
                global::Android.Views.ViewGroup.LayoutParams.WrapContent));
            _tickerInner.AddView(_tickerText2, new LinearLayout.LayoutParams(
                global::Android.Views.ViewGroup.LayoutParams.WrapContent,
                global::Android.Views.ViewGroup.LayoutParams.WrapContent));

            // _tickerInner must be WrapContent so it expands to hold both copies side-by-side.
            // Clipping to the visible viewport is handled by _scoresTickerContainer (MatchParent).
            _scoresTickerContainer.SetClipChildren(true);
            _scoresTickerContainer.SetClipToPadding(true);
            _scoresTickerContainer.AddView(_tickerInner, new LinearLayout.LayoutParams(
                global::Android.Views.ViewGroup.LayoutParams.WrapContent,
                global::Android.Views.ViewGroup.LayoutParams.WrapContent));

            // Runnable-based scroll loop: runs every ~16ms (~60fps)
            _tickerHandler = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _tickerRunnable = new Java.Lang.Runnable(() =>
            {
                if (_tickerInner == null || _tickerText1 == null || _scoresTickerContainer == null) return;
                var text1Width = _tickerText1.Width;
                if (text1Width <= 0)
                {
                    PostDelayedCallback(_tickerHandler, _tickerRunnable, 32);
                    return;
                }

                var gap = _tickerText1.PaddingRight;
                var contentWidth = Math.Max(0, text1Width - gap);
                var viewportWidth = Math.Max(_scoresTickerContainer.Width, _scoresTickerContainer.MeasuredWidth)
                    - _scoresTickerContainer.PaddingLeft
                    - _scoresTickerContainer.PaddingRight;

                if (!TickerMarquee.ShouldLoop(contentWidth, viewportWidth))
                {
                    _tickerScrollX = 0f;
                    _tickerInner.TranslationX = 0f;
                    if (_tickerText2 != null)
                    {
                        _tickerText2.Visibility = global::Android.Views.ViewStates.Gone;
                    }

                    PostDelayedCallback(_tickerHandler, _tickerRunnable, 16);
                    return;
                }

                if (_tickerText2 != null)
                {
                    _tickerText2.Visibility = global::Android.Views.ViewStates.Visible;
                }

                var period = TickerMarquee.LoopPeriod(contentWidth, gap);
                _tickerScrollX = (float)TickerMarquee.WrapPositive(_tickerScrollX + _tickerPixelsPerFrame, period);
                _tickerInner.TranslationX = -_tickerScrollX;
                PostDelayedCallback(_tickerHandler, _tickerRunnable, 16);
            });

            var scoresParams = new FrameLayout.LayoutParams(
                global::Android.Views.ViewGroup.LayoutParams.MatchParent,
                global::Android.Views.ViewGroup.LayoutParams.WrapContent)
            {
                Gravity = global::Android.Views.GravityFlags.Bottom,
                LeftMargin = (int)(16 * density),
                RightMargin = (int)(16 * density),
                BottomMargin = _isTvDevice ? (int)(24 * density) : (int)(72 * density)
            };
            root.AddView(_scoresTickerContainer, scoresParams);

            _scoresTickerContainer.Clickable = true;
            _scoresTickerContainer.Click += (_, __) =>
            {
                if (!_isTvDevice)
                {
                    CycleScoresTickerMode();
                }
            };

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
            SubscribeToStreamSwitching();

            if (!string.IsNullOrEmpty(_m3u8Url))
            {
                _logger?.LogInformation("[NativeVideoActivity] Starting initial playback: {Url}", _m3u8Url);
                AttachViaSession(_m3u8Url, usedCachedUrl: true);
            }

            HideOverlayAnimated();
        }

        private void SubscribeToStreamSwitching()
        {
            try
            {
                if (_switching == null)
                {
                    return;
                }

                _healthySub = _switching.HealthyStreamsUpdated.Subscribe(_ =>
                {
                    try
                    {
                        SyncHealthyStreamCount();
                        RunOnUiThread(() =>
                        {
                            UpdateOverlayFromCurrentStream();
                            ShowStreamToastIfNeeded();
                        });
                    }
                    catch (Exception ex) { LogIgnored("HealthyStreamsUpdated", ex); }
                });

                _indexSub = _switching.CurrentStreamIndexChanged.Subscribe(_ =>
                {
                    try
                    {
                        RunOnUiThread(() =>
                        {
                            UpdateOverlayFromCurrentStream();
                            TrySwitchToCurrentStream();
                            ShowStreamToastIfNeeded();
                        });
                    }
                    catch (Exception ex) { LogIgnored("CurrentStreamIndexChanged", ex); }
                });

                UpdateOverlayFromCurrentStream();
                SyncHealthyStreamCount();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed to subscribe to stream switching");
            }
        }

        private void TrySwitchToCurrentStream()
        {
            try
            {
                if (_suppressIndexDrivenSwitch)
                {
                    return;
                }

                var current = _switching?.GetCurrentStream();
                var url = current?.ResolvedM3U8Url;
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                if (string.Equals(_m3u8Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _logger?.LogInformation("[NativeVideoActivity] Switching player to current stream URL {Url}", url);
                AttachViaSession(url, usedCachedUrl: true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[NativeVideoActivity] Failed switching to current stream URL");
            }
        }

        public void SwitchToStreamUrl(string m3u8Url) => AttachViaSession(m3u8Url, usedCachedUrl: true);

        private void StopAndReleasePlayer(bool release)
        {
            try
            {
                if (_player == null) return;

                try { _player.PlayWhenReady = false; } catch { }
                try { _player.Pause(); } catch { }
                try { _player.Stop(); } catch { }

                if (release)
                {
                    try
                    {
                        if (_playerListener != null)
                        {
                            _player.RemoveListener(_playerListener);
                        }
                    }
                    catch { }

                    try { _player.Release(); } catch { }
                    _player = null;
                    _playerView = null;
                }
            }
            catch { }
        }

        private void DisposeSubscriptions()
        {
            try { _healthySub?.Dispose(); } catch { }
            _healthySub = null;
            try { _indexSub?.Dispose(); } catch { }
            _indexSub = null;
            try { _gamesSub?.Dispose(); } catch { }
            _gamesSub = null;
        }

        private void ReportPlaybackClosed(string message)
        {
            if (_playbackResultReported) return;
            _playbackResultReported = true;
            try { VardyParty.Platforms.Android.AndroidVideoPlayerService.ReportPlaybackResult(new PlaybackResult { Success = false, Message = message }); }
            catch (Exception ex) { LogIgnored("ReportPlaybackResult", ex); }
        }

        protected override void OnPause()
        {
            StopAndReleasePlayer(release: false);
            base.OnPause();
        }

        protected override void OnStop()
        {
            StopAndReleasePlayer(release: false);
            base.OnStop();
        }

        protected override void OnDestroy()
        {
            try
            {
                RemoveCallback(_tickerHandler, _tickerRunnable);
                RemoveCallback(_streamToastHandler, _streamToastRunnable);
                _healthReportTimer?.Dispose();
                _healthReportTimer = null;
                RemoveCallback(_overlayHandler, _overlayHideRunnable);
                DisposeSubscriptions();
                StopAndReleasePlayer(release: true);
                try { _switching?.Cleanup(); } catch { }
                ReportPlaybackClosed("Video player closed");
            }
            finally
            {
                base.OnDestroy();
            }
        }

        private static void RemoveCallback(global::Android.OS.Handler? handler, Java.Lang.IRunnable? runnable)
        {
            if (handler is null || runnable is null)
                return;
            handler.RemoveCallbacks(runnable);
        }

        private static void PostDelayedCallback(global::Android.OS.Handler? handler, Java.Lang.IRunnable? runnable, long delayMs)
        {
            if (handler is null || runnable is null)
                return;
            handler.PostDelayed(runnable, delayMs);
        }

        // Hide system UI (status bar and navigation bar) for full-screen video experience
        private void HideSystemUI()
        {
            try
            {
                var window = Window;
                if (window == null) return;

                // Hide status bar and navigation bar for immersive full-screen video
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    // SetDecorFitsSystemWindows is obsolete on API 35+ where edge-to-edge is the default.
                    if (!OperatingSystem.IsAndroidVersionAtLeast(35))
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
            catch (Exception ex) { LogIgnored("UpdateMetrics", ex); }

            return metrics;
        }

        // Marshal a function onto the Android UI thread and return its result as a Task.
        private Task<T> RunOnUiThreadAsync<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                RunOnUiThread(() =>
                {
                    try { tcs.TrySetResult(func()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                });
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
            return tcs.Task;
        }

        private void StartHealthReporting()
        {
            if (_healthReporter == null) return;
            _healthReportTimer?.Dispose();
            _healthReportTimer = new Timer(async _ =>
            {
                try
                {
                    // BuildPlaybackMetrics reads ExoPlayer properties which must be accessed on the
                    // main thread. Marshal the read to the UI thread, then continue on the pool thread.
                    var metrics = await RunOnUiThreadAsync(() => BuildPlaybackMetrics());
                    var window = _session.MetricsWindow;
                    window.ResetIfExpired();
                    if (window.BufferingEvents > 0)
                    {
                        metrics.IsBuffering = true;
                    }

                    await _healthReporter.ReportPlaybackMetricsAsync(_m3u8Url, _refererUrl, metrics: metrics);

                    var generation = CurrentAttachGeneration;
                    var bitrate = metrics.BitrateKbps;
                    var buffering = metrics.IsBuffering;
                    RunOnUiThread(() =>
                        _engine.Raise(MediaEngineEvent.Metrics(generation, bitrate, buffering)));
                }
                catch (Exception ex)
                {
                    LogIgnored("StartHealthReporting", ex);
                }
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void ReportPlaybackStarted()
        {
            if (_healthReporter == null) return;
            try
            {
                var metrics = BuildPlaybackMetrics();
                _ = _healthReporter.ReportPlaybackStartedAsync(_m3u8Url, _refererUrl, metrics: metrics);
            }
            catch (Exception ex)
            {
                LogIgnored("ReportPlaybackStarted", ex);
            }
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
                catch (Exception ex) { _activity.LogIgnored("OnPlaybackStateChanged", ex); }

                // STATE_READY = 3
                if (playbackState == PlayerStateReady)
                {
                    _activity._isBuffering = false;
                    _activity.HideBufferingIndicator();
                    _activity._isPreparing = false;
                    _activity._logger?.LogInformation("[NativeVideoActivity] Player ready");
                    _activity._engine.Raise(MediaEngineEvent.Ready(_activity.CurrentAttachGeneration));
                    // Don't report yet - wait for OnTracksChanged to extract real metadata
                    _activity.StartHealthReporting();
                    // Update overlay UI now that player is ready
                    var info = _activity.BuildOverlayInfoFromCurrentStream();
                    if (info != null)
                    {
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
                        catch (Exception ex) { _activity.LogIgnored("CaptureVideoSize", ex); }
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
                    catch (Exception ex) { _activity.LogIgnored("UpdateOverlayOnStateChange", ex); }
                }

                if (playbackState == PlayerStateEnded)
                {
                    _activity._logger?.LogInformation("[NativeVideoActivity] Playback ended");
                    _metadataReported = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(_activity._m3u8Url) && _activity._inMemoryManifestMap != null)
                        {
                            _activity._inMemoryManifestMap.TryRemove(_activity._m3u8Url, out _);
                            global::Android.Util.Log.Info("VardyParty", $"[NativeVideoActivity] Cleared in-memory manifest for {_activity._m3u8Url}");
                        }
                    }
                    catch (Exception ex) { _activity.LogIgnored("ClearInMemoryManifest", ex); }

                    _activity._engine.Raise(MediaEngineEvent.Ended(_activity.CurrentAttachGeneration));
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
                        _activity._engine.Raise(MediaEngineEvent.Buffering(_activity.CurrentAttachGeneration, true));
                    }
                    else if (_activity._player?.PlaybackState == PlayerStateReady)
                    {
                        _activity._playbackStateText = VardyParty.Resources.Strings.Resources.StatusPlaying;
                        _activity._isBuffering = false;
                        _activity.HideBufferingIndicator();
                        _activity._engine.Raise(MediaEngineEvent.Buffering(_activity.CurrentAttachGeneration, false));
                    }

                    var last = _activity._lastOverlayInfo;
                    if (last != null)
                    {
                        _activity.RunOnUiThread(() => _activity.UpdateOverlayText(last));
                    }
                }
                catch (Exception ex) { _activity.LogIgnored("OnIsLoadingChanged", ex); }
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
            public void OnPlayerErrorChanged(PlaybackException? error)
            {
                // ExoPlayer calls this with null to clear the previous error when a new source
                // is prepared. Treat null as a no-op to avoid spurious auto-switches.
                if (error == null) return;
                try
                {
                    var message = error.Message ?? "Playback error";
                    _activity._playbackStateText = VardyParty.Resources.Strings.Resources.StatusBuffering;
                    _activity._isBuffering = false;
                    _activity._isPreparing = false;
                    _activity.HideBufferingIndicator();
                    _activity._engine.Raise(MediaEngineEvent.Error(_activity.CurrentAttachGeneration, message));
                }
                catch (Exception ex) { _activity.LogIgnored("OnPlayerErrorChanged", ex); }
            }
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
