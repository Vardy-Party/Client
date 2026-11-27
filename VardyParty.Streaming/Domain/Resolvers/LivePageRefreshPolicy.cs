using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Extra local-service sweeps while a game is open. The games API catalog does
/// not gain pages; the local service can start returning playlists and chips
/// for pages it had nothing for earlier.
/// </summary>
public static class LivePageRefreshPolicy
{
    /// <summary>
    /// Once two streams are playable, a sweep is followed by this quiet period.
    /// Fewer than two keeps scanning after <see cref="ScanFloor"/>.
    /// </summary>
    public static readonly TimeSpan RestAfterBackup = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum gap between sweep passes while fewer than two streams work.
    /// The same length as the shortest CDN recheck, so a scanning pass does not
    /// start again the moment the previous one finishes and cannot outrun rule 13.
    /// A page the CDN loop is already waiting on is left out of that pass.
    /// </summary>
    public static readonly TimeSpan ScanFloor = CdnReachabilityPolicy.FirstRecheckWithoutBackup;

    /// <summary>
    /// One local-service slot per extra sweep, so a stream switch can still take the other.
    /// </summary>
    public const int SlotsPerSweep = 1;

    /// <summary>No backup yet: keep asking the local service.</summary>
    public static bool KeepScanning(int workingStreams) => workingStreams < 2;

    /// <summary>
    /// How long to wait before the next sweep. <see cref="ScanFloor"/> while fewer
    /// than two streams work. Five minutes once a backup is in hand, unless the
    /// viewer is already watching and kickoff is sooner than that rest: that sweep
    /// starts when the game starts.
    /// </summary>
    public static TimeSpan DelayBeforeNextSweep(int workingStreams, TimeSpan untilKickoff, bool viewerInPlayer)
    {
        if (KeepScanning(workingStreams))
            return ScanFloor;

        if (viewerInPlayer && untilKickoff > TimeSpan.Zero && untilKickoff < RestAfterBackup)
            return untilKickoff;

        return RestAfterBackup;
    }

    /// <summary>Time until <paramref name="game"/> kicks off. Zero when it has started, finished, or has no start.</summary>
    public static TimeSpan UntilKickoff(Game? game, DateTime utcNow)
    {
        if (game is null || game.IsFinished || game.Start == default)
            return TimeSpan.Zero;

        var start = game.Start.Kind == DateTimeKind.Utc ? game.Start : game.Start.ToUniversalTime();
        var until = start - utcNow;
        return until > TimeSpan.Zero ? until : TimeSpan.Zero;
    }

    /// <summary>
    /// Catalog pages to ask the local service about again, one row per page URL.
    /// An MP page is an unlabeled seed so a fresh answer can list chips that were
    /// not there before. Countdown is cleared: that flag came from the API and
    /// will not change, and the local service is what can now have a playlist.
    /// Pages that are already playable are asked again. A later answer can show
    /// they are no longer healthy, and switching then passes over them.
    /// <paramref name="waitingOnCdnRecheck"/> pages are left to the CDN back-off
    /// (rule 13) and are not asked on this pass.
    /// </summary>
    public static IReadOnlyList<StreamModel> PagesToAskAgain(
        IEnumerable<StreamModel> catalogRows,
        IEnumerable<StreamModel>? waitingOnCdnRecheck = null)
    {
        HashSet<string>? waiting = null;
        if (waitingOnCdnRecheck != null)
        {
            waiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in waitingOnCdnRecheck)
            {
                if (string.IsNullOrWhiteSpace(row.Url))
                    continue;

                waiting.Add(StreamHealthIdentity.NormalizeStreamUrl(row.Url));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<StreamModel>();
        foreach (var row in catalogRows)
        {
            if (string.IsNullOrWhiteSpace(row.Url))
                continue;

            var key = StreamHealthIdentity.NormalizeStreamUrl(row.Url);
            if (!seen.Add(key))
                continue;

            if (waiting != null && waiting.Contains(key))
                continue;

            pages.Add(CopyForLocalAsk(row));
        }

        return pages;
    }

    /// <summary>
    /// Same catalog page, and the same chip when either side names one.
    /// A nameless row matches only another nameless row on that page.
    /// </summary>
    public static bool SameStream(StreamModel pool, StreamModel probed)
    {
        if (string.IsNullOrWhiteSpace(pool.Url) || string.IsNullOrWhiteSpace(probed.Url))
            return false;

        if (!string.Equals(
                StreamHealthIdentity.NormalizeStreamUrl(pool.Url),
                StreamHealthIdentity.NormalizeStreamUrl(probed.Url),
                StringComparison.OrdinalIgnoreCase))
            return false;

        var probedName = StreamHealthIdentity.GetStreamName(probed);
        var poolName = StreamHealthIdentity.GetStreamName(pool);
        if (string.IsNullOrEmpty(probedName))
            return string.IsNullOrEmpty(poolName);

        return string.Equals(poolName, probedName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pool rows a completed page probe changes for switching. Deferred is not a
    /// verdict. A row is skipped when this probe's definitive answers do not
    /// include it as healthy, and restored when they do. The picture that is
    /// already on screen is left playing; Next and Previous pass over a skip.
    /// </summary>
    public static SweepSwitchingUpdate SwitchingAfterPageProbe(
        IEnumerable<EnrichedStream> pool,
        IEnumerable<EnrichedStream> verdicts)
    {
        var byPage = new Dictionary<string, List<EnrichedStream>>(StringComparer.OrdinalIgnoreCase);
        foreach (var verdict in verdicts)
        {
            var url = verdict.Stream?.Url;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var key = StreamHealthIdentity.NormalizeStreamUrl(url);
            if (key.Length == 0)
                continue;

            if (!byPage.TryGetValue(key, out var list))
            {
                list = [];
                byPage[key] = list;
            }

            list.Add(verdict);
        }

        var skip = new List<EnrichedStream>();
        var restore = new List<EnrichedStream>();
        if (byPage.Count == 0)
            return new SweepSwitchingUpdate(skip, restore);

        foreach (var (key, pageVerdicts) in byPage)
        {
            if (!TryReadPageVerdict(pageVerdicts, out var verdict))
                continue;

            foreach (var row in pool)
            {
                var url = row.Stream?.Url;
                if (string.IsNullOrWhiteSpace(url))
                    continue;
                if (!string.Equals(StreamHealthIdentity.NormalizeStreamUrl(url), key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (RowCameBackHealthy(row, verdict))
                    restore.Add(row);
                else if (!RowAnswerWasDeferred(row, verdict))
                    skip.Add(row);
            }
        }

        return new SweepSwitchingUpdate(skip, restore);
    }

    /// <summary>Which pool rows a finished page probe drops from, or puts back into, the switch order.</summary>
    public readonly record struct SweepSwitchingUpdate(
        IReadOnlyList<EnrichedStream> Skip,
        IReadOnlyList<EnrichedStream> Restore);

    private readonly struct PageVerdict
    {
        public bool PageHealthy { get; init; }
        public HashSet<string> HealthyNames { get; init; }
        public HashSet<string> FailedNames { get; init; }
        public HashSet<string> DeferredNames { get; init; }
    }

    private static bool TryReadPageVerdict(List<EnrichedStream> verdicts, out PageVerdict verdict)
    {
        var healthyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageHealthy = false;
        var definitive = false;

        foreach (var item in verdicts)
        {
            var stream = item.Stream;
            if (stream == null)
                continue;

            var name = StreamHealthIdentity.GetStreamName(stream);
            var named = !string.IsNullOrEmpty(name);
            switch (item.Status)
            {
                case StreamResolutionStatus.Healthy:
                    definitive = true;
                    if (named)
                        healthyNames.Add(name!);
                    else
                        pageHealthy = true;
                    break;
                case StreamResolutionStatus.Failed:
                    definitive = true;
                    if (named)
                        failedNames.Add(name!);
                    break;
                case StreamResolutionStatus.Deferred:
                    if (named)
                        deferredNames.Add(name!);
                    break;
            }
        }

        verdict = new PageVerdict
        {
            PageHealthy = pageHealthy,
            HealthyNames = healthyNames,
            FailedNames = failedNames,
            DeferredNames = deferredNames
        };
        return definitive;
    }

    private static bool RowCameBackHealthy(EnrichedStream row, PageVerdict verdict)
    {
        var name = row.Stream == null ? null : StreamHealthIdentity.GetStreamName(row.Stream);
        if (!string.IsNullOrEmpty(name))
            return verdict.HealthyNames.Contains(name);

        return verdict.PageHealthy;
    }

    private static bool RowAnswerWasDeferred(EnrichedStream row, PageVerdict verdict)
    {
        var name = row.Stream == null ? null : StreamHealthIdentity.GetStreamName(row.Stream);
        if (string.IsNullOrEmpty(name))
            return false;

        return verdict.DeferredNames.Contains(name)
               && !verdict.HealthyNames.Contains(name)
               && !verdict.FailedNames.Contains(name);
    }

    private static StreamModel CopyForLocalAsk(StreamModel row)
    {
        var mp = row.RequiresV2StreamSelection;
        return new StreamModel
        {
            Url = row.Url,
            Channel = mp ? string.Empty : row.Channel,
            Source = row.Source,
            ResolutionStrategy = row.ResolutionStrategy,
            Quality = row.Quality,
            Language = row.Language,
            Reputation = row.Reputation,
            Ads = row.Ads,
            StreamStatus = string.Empty,
            PlayerStream = string.Empty,
            PlayerStreams = []
        };
    }
}
