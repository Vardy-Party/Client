using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VardyParty.Auth;
using VardyParty.Catalog;
using VardyParty.Hosting;
using VardyParty.Kernel;
using VardyParty.Streaming;
using StreamModel = VardyParty.Kernel.Stream;

namespace StreamHealthCheckerTool;

/// <summary>
/// Stream test harness. Uses the same composition as the app heads
/// (AddVardyParty + AddVardyPartyHttpClients), so it exercises the real
/// StreamResolver, LocalLanPlayService (UDP discovery) and StreamHealthChecker.
///
///   StreamHealthCheckerTool                 every live game from /new
///   StreamHealthCheckerTool --game "Home"   only games whose Home/Away contains the text
///   StreamHealthCheckerTool &lt;pageUrl&gt;       one stream page
///
/// Api:HeadlessBaseUrl and Auth0 come from user-secrets (or VARDYPARTY_HARNESS_API).
/// Sign-in is the Auth0 device flow; tokens stay in memory only.
/// </summary>
internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddUserSecrets<Program>(optional: true)
            .Build();
        var apiOverride = Environment.GetEnvironmentVariable("VARDYPARTY_HARNESS_API");
        if (!string.IsNullOrWhiteSpace(apiOverride))
            configuration["Api:HeadlessBaseUrl"] = apiOverride.Trim();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b =>
        {
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
            b.SetMinimumLevel(LogLevel.Information);
            b.AddFilter("System.Net.Http", LogLevel.Warning);
        });
        services.Configure<Auth0Settings>(configuration.GetSection(Auth0Settings.SectionName));
        services.Configure<APISettings>(configuration.GetSection(APISettings.SectionName));
        services.Configure<GamesApiSettings>(configuration.GetSection(GamesApiSettings.SectionName));
        services.Configure<StreamHealthSettings>(configuration.GetSection(StreamHealthSettings.SectionName));
        services.Configure<BbcFixturesSettings>(configuration.GetSection(BbcFixturesSettings.SectionName));
        services.AddSingleton<ILeagueFilterPreferencesStore, InMemoryLeagueFilterPreferencesStore>();
        services.AddVardyParty();
        services.AddSingleton<HarnessAuthSession>();
        services.AddSingleton<IAuthTokenProvider>(sp => sp.GetRequiredService<HarnessAuthSession>());
        services.AddVardyPartyHttpClients();

        await using var sp = services.BuildServiceProvider();
        var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Harness");
        var baseUrl = sp.GetRequiredService<IOptions<APISettings>>().Value.HeadlessBaseUrl;
        Console.WriteLine($"[Harness] API {baseUrl}  started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        var auth = sp.GetRequiredService<HarnessAuthSession>();
        if (!await auth.SignInAsync())
            return 2;

        var started = Stopwatch.StartNew();
        var resolver = sp.GetRequiredService<IStreamResolver>();
        var api = sp.GetRequiredService<IApiService>();
        var rows = new List<Row>();

        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            var stream = new StreamModel { Url = args[0].Trim(), Channel = args[0].Trim() };
            await ResolveGameAsync("(single url)", [stream], resolver, rows, log);
        }
        else
        {
            var filter = args.Length > 1 && args[0] == "--game" ? args[1] : null;
            var games = await sp.GetRequiredService<IGamesCatalogApi>().GetAllGamesAsync(forceRefresh: true);
            var now = DateTime.UtcNow;
            var live = games.Values.SelectMany(g => g)
                .Where(g => !g.IsFinished && !g.IsPostponed)
                .Where(g => g.IsLiveForOrdering || IsInLiveWindow(g.Start, now))
                .Where(g => filter is null
                            || g.Home.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || g.Away.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .GroupBy(g => g.FixtureKey).Select(g => g.First())
                .ToList();
            Console.WriteLine($"[Harness] /new: {games.Values.Sum(g => g.Count)} games, {live.Count} live");
            foreach (var game in live)
            {
                var match = $"{game.Home} v {game.Away} ({game.ApiLeague})";
                var response = await api.GetStreamsAsync(game.ApiLeague, game.Home, game.Away, forceRefresh: true);
                var streams = response?.Streams ?? [];
                Console.WriteLine($"[Harness] {match}: {streams.Count} stream(s)");
                if (streams.Count > 0)
                    await ResolveGameAsync(match, streams, resolver, rows, log);
            }
        }

        await ProbeAsync(rows, sp, log);
        Print(rows, started.Elapsed);
        return 0;
    }

    private static bool IsInLiveWindow(DateTime start, DateTime nowUtc)
    {
        var s = start.Kind == DateTimeKind.Local ? start.ToUniversalTime() : start;
        return s <= nowUtc.AddMinutes(5) && s >= nowUtc.AddMinutes(-150);
    }

    /// <summary>Same passes as the orchestrator: first sweep, then deferred rows after 5s and 15s.</summary>
    private static async Task ResolveGameAsync(string match, List<StreamModel> streams, IStreamResolver resolver,
        List<Row> rows, ILogger log)
    {
        TimeSpan[] requeue = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
        var pending = streams;
        for (var pass = 0; pending.Count > 0; pass++)
        {
            var deferred = new List<StreamModel>();
            var sw = Stopwatch.StartNew();
            await foreach (var e in resolver.ResolveStreamsIncrementallyAsync(pending, 2))
            {
                var row = rows.FirstOrDefault(r => r.Match == match && ReferenceEquals(r.Enriched.Stream, e.Stream));
                if (row is null) rows.Add(row = new Row(match, e));
                row.Enriched = e;
                row.Passes.Add($"{e.Status}@{sw.Elapsed.TotalSeconds:0}s");
                if (e.Status == StreamResolutionStatus.Deferred) deferred.Add(e.Stream);
            }
            if (deferred.Count == 0 || pass >= requeue.Length) break;
            log.LogInformation("[Harness] Re-queue {Count} deferred row(s) in {Delay}s", deferred.Count, requeue[pass].TotalSeconds);
            await Task.Delay(requeue[pass]);
            pending = deferred;
        }
    }

    /// <summary>
    /// For every resolved row: TCP 443 reachability of the playlist and segment
    /// hosts, and a 64 KB segment read classified by SegmentPayload (HasVideoData).
    /// One-shot URLs may already be burnt by the health check; that shows as a
    /// playlist error here, not as a verdict.
    /// </summary>
    private static async Task ProbeAsync(List<Row> rows, IServiceProvider sp, ILogger log)
    {
        using var http = new HttpClient(DualStackSocketsHttpHandler.Create(
            hostNameResolver: sp.GetRequiredService<IHostNameResolver>())) { Timeout = TimeSpan.FromSeconds(15) };
        var tcp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => !string.IsNullOrEmpty(r.Enriched.ResolvedM3U8Url)))
        {
            var e = row.Enriched;
            var m3u8 = new Uri(e.ResolvedM3U8Url!);
            row.M3u8Host = m3u8.Host;
            row.Tcp443 = await TcpAsync(m3u8, tcp);
            try
            {
                var segment = e.RewrittenSegments?.FirstOrDefault();
                if (segment is null)
                {
                    var playlist = await GetTextAsync(http, m3u8, e);
                    var lines = playlist.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                    if (playlist.Contains("#EXT-X-STREAM-INF") && lines.Count > 0)
                    {
                        var variant = ResolvePlaylistUri(m3u8, lines[0]);
                        playlist = await GetTextAsync(http, variant, e);
                        lines = playlist.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                        m3u8 = variant;
                    }
                    segment = lines.Count > 0 ? ResolvePlaylistUri(m3u8, lines[^1]).AbsoluteUri : null;
                }
                if (segment is null) { row.VideoNote = "no segments"; continue; }
                var segUri = new Uri(segment);
                row.SegmentHost = segUri.Host;
                if (!string.Equals(segUri.Host, m3u8.Host, StringComparison.OrdinalIgnoreCase))
                    row.Tcp443 += $" / seg {await TcpAsync(segUri, tcp)}";
                using var req = Request(segUri, e);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, SegmentPayload.ProbeBytes - 1);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) { row.VideoNote = $"segment HTTP {(int)resp.StatusCode}"; continue; }
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var kind = SegmentPayload.Classify(bytes.AsSpan(0, Math.Min(bytes.Length, SegmentPayload.ProbeBytes)));
                row.HasVideoData = SegmentPayload.IsPlayable(kind);
                row.VideoNote = kind.ToString();
            }
            catch (Exception ex)
            {
                row.VideoNote = $"{ex.GetType().Name}: {ex.Message}";
                log.LogDebug(ex, "[Harness] probe failed for {Url}", e.ResolvedM3U8Url);
            }
        }
    }

    private static Uri ResolvePlaylistUri(Uri playlist, string reference) =>
        HlsUriResolver.TryResolve(playlist, reference, out Uri? resolved)
            ? resolved
            : throw new InvalidOperationException($"unresolvable playlist URI {reference}");

    private static async Task<string> TcpAsync(Uri uri, Dictionary<string, string> cache)
    {
        if (cache.TryGetValue(uri.Host, out var known)) return known;
        var port = uri.Port > 0 ? uri.Port : 443;
        string result;
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(uri.Host, port, cts.Token);
            result = $"tcp{port} ok {sw.ElapsedMilliseconds}ms";
        }
        catch (OperationCanceledException) { result = $"tcp{port} TIMEOUT"; }
        catch (Exception ex) { result = $"tcp{port} {ex.GetType().Name}"; }
        return cache[uri.Host] = result;
    }

    private static HttpRequestMessage Request(Uri uri, EnrichedStream e)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (e.RequestHeaders != null)
            foreach (var (k, v) in e.RequestHeaders)
                if (!k.StartsWith(':') && !k.Equals("host", StringComparison.OrdinalIgnoreCase)
                    && !k.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                    && !k.Equals("range", StringComparison.OrdinalIgnoreCase))
                    req.Headers.TryAddWithoutValidation(k, v);
        if (!req.Headers.Contains("Referer") && !string.IsNullOrEmpty(e.Referer))
            req.Headers.TryAddWithoutValidation("Referer", e.Referer);
        return req;
    }

    private static async Task<string> GetTextAsync(HttpClient http, Uri uri, EnrichedStream e)
    {
        using var req = Request(uri, e);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"playlist HTTP {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync();
    }

    private static void Print(List<Row> rows, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine("RESULT|match|source|channel|m3u8 host|segment host|outcome|why|HasVideoData|video note|tcp443|passes");
        foreach (var r in rows)
        {
            var e = r.Enriched;
            Console.WriteLine(string.Join('|', "RESULT", r.Match, e.Stream.ResolveCatalogSource(), e.Stream.Channel,
                r.M3u8Host ?? "-", r.SegmentHost ?? "-", e.Status, e.ErrorMessage ?? (e.Health?.GetQualityLabel() ?? ""),
                r.HasVideoData?.ToString() ?? "-", r.VideoNote ?? "-", r.Tcp443 ?? "-", string.Join(' ', r.Passes)));
        }
        Console.WriteLine();
        Console.WriteLine($"[Harness] Rows {rows.Count}: " + string.Join(", ",
            rows.GroupBy(r => r.Enriched.Status).Select(g => $"{g.Key} {g.Count()}")) +
            $"; HasVideoData true {rows.Count(r => r.HasVideoData == true)}; run time {elapsed:mm\\:ss}");
    }

    private sealed class Row(string match, EnrichedStream enriched)
    {
        public string Match { get; } = match;
        public EnrichedStream Enriched { get; set; } = enriched;
        public List<string> Passes { get; } = [];
        public string? M3u8Host { get; set; }
        public string? SegmentHost { get; set; }
        public string? Tcp443 { get; set; }
        public bool? HasVideoData { get; set; }
        public string? VideoNote { get; set; }
    }
}

/// <summary>Auth0 device sign-in for the harness. Tokens are kept in memory only, never written to disk.</summary>
internal sealed class HarnessAuthSession(
    ILogger<HarnessAuthSession> logger,
    IOptions<Auth0Settings> settings,
    IAuth0OAuthClient oauth) : Auth0TokenSession(logger, settings, oauth)
{
    public async Task<bool> SignInAsync()
    {
        var start = await StartDeviceLoginAsync();
        if (start is null) return false;
        var code = start.DeviceCode;
        Console.WriteLine($"[Harness] SIGN IN: open {code.VerificationUriComplete ?? code.VerificationUri} and confirm code {code.UserCode} (expires {code.ExpiresAt.ToLocalTime():HH:mm})");
        var result = await PollDeviceLoginAsync(code);
        Console.WriteLine(result.IsSuccess ? "[Harness] Signed in." : $"[Harness] Sign-in failed: {result.Error}");
        return result.IsSuccess;
    }

    public override Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new AuthLoginResult(false, null, "Use the device flow."));

    protected override Task LoadPersistedTokensAsync() => Task.CompletedTask;
    protected override Task PersistTokensAsync() => Task.CompletedTask;
    protected override Task ClearPersistedTokensAsync() => Task.CompletedTask;
}