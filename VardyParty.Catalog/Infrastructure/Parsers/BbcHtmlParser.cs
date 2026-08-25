using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VardyParty.Models;

namespace VardyParty.Catalog;

public class BbcHtmlParser(ILogger<BbcHtmlParser> logger, IBbcJsonParser bbcJsonParser) : IBbcHtmlParser
{
    // Real BBC event cards are ~2.5–4KB apart; cap avoids scanning hundreds of KB of trailing scripts.
    private const int MaxGameBlockChars = 6144;
    private const int CancelCheckEveryGames = 16;
    private const string EscapedStartDateTimeMarker = "\\\"startDateTime\\\":\\\"";
    private const string EscapedIdMarker = "\\\"id\\\":\\\"";
    private const string UnescapedStartDateTimeMarker = "\"startDateTime\":\"";
    private const string UnescapedIdMarker = "\"id\":\"";
    private const string DataEventIdMarker = "data-event-id=";
    private const string H2Marker = "<h2";
    // BBC competition headings are often <h3>, not <h2>; treat both as card boundaries.
    private const string H3Marker = "<h3";
    // MatchProgressContainer is compact; a long probe reaches the next fixture's "Full time".
    private const int ProgressProbeMaxChars = 500;

    private static readonly Regex AggScoreAltRegex = new(@"Aggregate\s+score[^<]*(?<h>\d+)\s*,[^<]*(?<a>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InjuryTimeApostropheRegex = new(@"(\d+)(?:&#x27;|&#39;|')\s*\+\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InjuryTimePlainRegex = new(@"(\d+)\s*\+\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MinuteApostropheRegex = new(@"(?<m>\d+)\s*'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MinuteWordsRegex = new(@"(?<m>\d+)\s*minutes?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyledPeriodMinuteRegex = new(@"<div[^>]*>(\d+)(?:&#x27;|&#39;|')</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PenaltiesWinRegex = new(@"(?<winner>[^<>\n]{1,100}?)\s+win\s+(?<w>\d+)\s*-\s*(?<l>\d+)\s+on\s+penalties", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OrdinalStatusRegex = new(@"\b\d+\s*(st|nd|rd|th)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Simple, robust regex-based parser. Keeps memory footprint low.
    public List<BbcFixture> ParseHtml(string html, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrEmpty(html)) return [];

        // One stream pass over __INITIAL_DATA__ for status + kickoffs (avoids a second full-page scan).
        var swMap = System.Diagnostics.Stopwatch.StartNew();
        var (eventStatusMap, eventKickoffMap) = bbcJsonParser.BuildEventMapsStreaming(html, cancellationToken);
        swMap.Stop();

        cancellationToken.ThrowIfCancellationRequested();

        if (eventStatusMap.Count == 0)
        {
            logger.LogWarning("[BBC] Event status map empty after streaming parse; continuing with HTML-only status");
        }

        // Fallback only when INITIAL_DATA lacked kickoffs (synthetic/unit-test HTML).
        var swKickoff = System.Diagnostics.Stopwatch.StartNew();
        if (eventKickoffMap.Count == 0)
        {
            eventKickoffMap = BuildEventKickoffMap(html);
        }
        swKickoff.Stop();

        var capacity = Math.Max(eventStatusMap.Count, eventKickoffMap.Count);
        if (capacity <= 0) capacity = 64;
        var list = new List<BbcFixture>(capacity);

        var swSerial = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ScanHtmlSerial(html, list, eventStatusMap, eventKickoffMap, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BBC] Serial scan failed");
        }
        swSerial.Stop();

        sw.Stop();

        logger.LogInformation(
            "[BBC] Parsing stats: MapStream={Map}ms StatusMap={StatusCount} Kickoffs={KickoffCount} KickoffFallback={Kickoff}ms SerialScan={Serial}ms Fixtures={FixtureCount} Total={Total}ms",
            swMap.ElapsedMilliseconds,
            eventStatusMap.Count,
            eventKickoffMap.Count,
            swKickoff.ElapsedMilliseconds,
            swSerial.ElapsedMilliseconds,
            list.Count,
            sw.ElapsedMilliseconds);

        return list;
    }

    private static Dictionary<string, DateTime> BuildEventKickoffMap(string html)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return map;

        ScanKickoffMarkers(html, EscapedStartDateTimeMarker, EscapedIdMarker, '\\', map);
        ScanKickoffMarkers(html, UnescapedStartDateTimeMarker, UnescapedIdMarker, '"', map);
        return map;
    }

    private static void ScanKickoffMarkers(
        string html,
        string startDateTimeMarker,
        string idMarker,
        char idTerminator,
        Dictionary<string, DateTime> map)
    {
        var pos = 0;
        while (pos < html.Length)
        {
            var startIdx = html.IndexOf(startDateTimeMarker, pos, StringComparison.Ordinal);
            if (startIdx < 0) break;

            var dtStart = startIdx + startDateTimeMarker.Length;
            var dtEnd = html.IndexOf(idTerminator, dtStart);
            if (dtEnd <= dtStart)
            {
                pos = startIdx + 1;
                continue;
            }

            var dtText = html.Substring(dtStart, dtEnd - dtStart);
            var searchWindow = Math.Min(4000, startIdx);
            var idIdx = html.LastIndexOf(idMarker, startIdx, searchWindow);
            if (idIdx >= 0)
            {
                var idStart = idIdx + idMarker.Length;
                var idEnd = html.IndexOf(idTerminator, idStart);
                if (idEnd > idStart)
                {
                    var id = html.Substring(idStart, idEnd - idStart);
                    if (id.StartsWith("s-", StringComparison.OrdinalIgnoreCase)
                        && !map.ContainsKey(id)
                        && BbcJsonParser.TryParseKickoffUtc(dtText, out var kickoffUtc))
                    {
                        map[id] = kickoffUtc;
                    }
                }
            }

            pos = dtEnd + 1;
        }
    }

    private void ScanHtmlSerial(
        string html,
        List<BbcFixture> list,
        Dictionary<string, (string periodLabel, string status, string statusComment)> eventStatusMap,
        Dictionary<string, DateTime> eventKickoffMap,
        CancellationToken cancellationToken)
    {
        // Single pass scanner over "<h2" / "data-event-id=" markers.
        int cursor = 0;
        string currentLeague = string.Empty;
        string currentH2League = string.Empty; // competition set by <h2>; h3 sub-rounds do not overwrite it
        int len = html.Length;
        int gamesParsed = 0;

        while (cursor < len)
        {
            if ((gamesParsed & (CancelCheckEveryGames - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int nextH2 = html.IndexOf(H2Marker, cursor, StringComparison.Ordinal);
            int nextH3 = html.IndexOf(H3Marker, cursor, StringComparison.Ordinal);
            int nextHeader = MinNonNegative(nextH2, nextH3);
            int nextGame = html.IndexOf(DataEventIdMarker, cursor, StringComparison.Ordinal);

            if (nextHeader < 0 && nextGame < 0) break;

            bool isHeader;
            int foundIdx;
            if (nextHeader >= 0 && nextGame >= 0)
            {
                isHeader = nextHeader < nextGame;
                foundIdx = isHeader ? nextHeader : nextGame;
            }
            else if (nextHeader >= 0)
            {
                isHeader = true;
                foundIdx = nextHeader;
            }
            else
            {
                isHeader = false;
                foundIdx = nextGame;
            }

            cursor = foundIdx;

            if (isHeader)
            {
                var isH3 = foundIdx == nextH3;
                var closeHeader = isH3 ? "</h3>" : "</h2>";
                int endHeader = html.IndexOf(closeHeader, cursor, StringComparison.OrdinalIgnoreCase);
                if (endHeader > cursor)
                {
                    int closeTag = html.IndexOf('>', cursor);
                    if (closeTag > cursor && closeTag < endHeader)
                    {
                        var content = html.Substring(closeTag + 1, endHeader - closeTag - 1);
                        var text = System.Net.WebUtility.HtmlDecode(StripTags(content));
                        if (!string.IsNullOrWhiteSpace(text) && !text.Contains("Scores & Fixtures", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!isH3)
                            {
                                // <h2> is always the competition name; reset h3 tracking.
                                currentH2League = text;
                                currentLeague = text;
                            }
                            else if (string.IsNullOrEmpty(currentH2League))
                            {
                                // <h3> only sets the league when there is no parent <h2> competition
                                // (some BBC pages use h3 as the top-level heading).
                                currentLeague = text;
                            }
                            // else: <h3> is a sub-round label (e.g. "1st Round") under an <h2> competition —
                            // ignore it so the competition name is preserved for the fixtures below.
                        }
                    }
                    cursor = endHeader + closeHeader.Length;
                }
                else
                {
                    cursor += 4;
                }
            }
            else
            {
                // Bound the card tightly. Without a cap, the last fixture scans hundreds of KB of trailing scripts.
                int searchFrom = cursor + DataEventIdMarker.Length;
                int cappedLimit = Math.Min(cursor + MaxGameBlockChars, len);
                int nextMarker1 = html.IndexOf(DataEventIdMarker, searchFrom, StringComparison.Ordinal);
                int nextMarker2 = html.IndexOf(H2Marker, searchFrom, StringComparison.Ordinal);
                int nextMarker3 = html.IndexOf(H3Marker, searchFrom, StringComparison.Ordinal);
                int limit = cappedLimit;
                if (nextMarker1 >= 0 && nextMarker1 < limit) limit = nextMarker1;
                if (nextMarker2 >= 0 && nextMarker2 < limit) limit = nextMarker2;
                if (nextMarker3 >= 0 && nextMarker3 < limit) limit = nextMarker3;

                string id = "";
                int quoteStart = html.IndexOf('"', searchFrom);
                if (quoteStart >= 0 && quoteStart < limit)
                {
                    int quoteEnd = html.IndexOf('"', quoteStart + 1);
                    if (quoteEnd > quoteStart && quoteEnd < limit)
                    {
                        id = html.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                    }
                }

                if (!string.IsNullOrEmpty(id))
                {
                    ParseGameNative(html, cursor, limit, id, currentLeague, list, eventStatusMap, eventKickoffMap);
                    gamesParsed++;
                }

                cursor = limit;
            }
        }
    }

    private static void ParseGameNative(
        string html,
        int start,
        int end,
        string id,
        string currentLeague,
        List<BbcFixture> list,
        Dictionary<string, (string periodLabel, string status, string statusComment)> eventStatusMap,
        Dictionary<string, DateTime> eventKickoffMap)
    {
        if (end - start <= 0) return;
        // Parse in-place on the page string (no per-card Substring allocation).
        ParseGameNativeCard(html, start, end, id, currentLeague, list, eventStatusMap, eventKickoffMap);
    }

    private static void ParseGameNativeCard(
        string html,
        int start,
        int end,
        string id,
        string currentLeague,
        List<BbcFixture> list,
        Dictionary<string, (string periodLabel, string status, string statusComment)> eventStatusMap,
        Dictionary<string, DateTime> eventKickoffMap)
    {
        // Extract fields using IndexOf within range
        string ExtractRange(string marker)
        {
            int rangeLen = end - start;
            if (rangeLen <= 0) return string.Empty;
            // BBC class tokens are stable camelCase — Ordinal is enough and cheaper on Android.
            int mIdx = html.IndexOf(marker, start, rangeLen, StringComparison.Ordinal);
            if (mIdx < 0) return string.Empty;

            int dIdx = html.IndexOf("DesktopValue", mIdx, end - mIdx, StringComparison.Ordinal);
            if (dIdx < 0) return string.Empty;

            int valStart = html.IndexOf('>', dIdx, end - dIdx);
            if (valStart < 0) return string.Empty;
            valStart++; // skip >

            if (valStart >= end) return string.Empty;

            int valEnd = html.IndexOf('<', valStart, end - valStart);
            if (valEnd < 0) return string.Empty;

            return html.Substring(valStart, valEnd - valStart);
        }

        var home = DecodeIfNeeded(ExtractRange("WithInlineFallback-TeamHome"));
        var away = DecodeIfNeeded(ExtractRange("WithInlineFallback-TeamAway"));

        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(away))
        {
            return;
        }

        string ExtractScore(string marker)
        {
            int rangeLen = end - start;
            if (rangeLen <= 0) return string.Empty;

            int mIdx = html.IndexOf(marker, start, rangeLen, StringComparison.Ordinal);
            if (mIdx < 0) return string.Empty;

            int valStart = html.IndexOf('>', mIdx, end - mIdx);
            if (valStart < 0) return string.Empty;
            valStart++;

            if (valStart >= end) return string.Empty;

            int valEnd = html.IndexOf('<', valStart, end - valStart);
            if (valEnd < 0) return string.Empty;

            return html.Substring(valStart, valEnd - valStart);
        }

        int? homeScore = TryParseInt(ExtractScore("HomeScore"));
        int? awayScore = TryParseInt(ExtractScore("AwayScore"));

        // Progress
        string progressInner = string.Empty;
        int rangeLen = end - start;
        if (rangeLen > 0)
        {
            int containerIdx = html.IndexOf("MatchProgressContainer", start, rangeLen, StringComparison.Ordinal);
            bool hasProgressContainer = false;
            int progressContainerEnd = -1;
            bool quickFt = false;
            bool quickHt = false;
            if (containerIdx >= 0)
            {
                hasProgressContainer = true;
                // Keep FT/HT probes inside this fixture's progress block. A long window can reach the
                // next fixture's "Full time" (often before the next data-event-id) and hide live games.
                var progressScopeEnd = Math.Min(end, containerIdx + ProgressProbeMaxChars);
                var probeLen = progressScopeEnd - containerIdx;
                if (probeLen > 0)
                {
                    quickFt = html.IndexOf(">FT<", containerIdx, probeLen, StringComparison.Ordinal) >= 0
                              || html.IndexOf("Full time", containerIdx, probeLen, StringComparison.OrdinalIgnoreCase) >= 0;
                    quickHt = html.IndexOf(">HT<", containerIdx, probeLen, StringComparison.Ordinal) >= 0
                              || html.IndexOf("Half time", containerIdx, probeLen, StringComparison.OrdinalIgnoreCase) >= 0;
                }

                if (quickFt)
                {
                    progressInner = "FT";
                    progressContainerEnd = progressScopeEnd;
                }
                else if (quickHt)
                {
                    progressInner = "HT";
                    progressContainerEnd = progressScopeEnd;
                }
                else
                {
                    int cStart = html.IndexOf('>', containerIdx) + 1;
                    if (cStart > 0)
                    {
                        int searchLimit = Math.Min(progressScopeEnd - cStart, ProgressProbeMaxChars);
                        if (searchLimit < 0) searchLimit = 0;
                        int cEnd = searchLimit > 0
                            ? html.IndexOf("</div>", cStart, searchLimit, StringComparison.Ordinal)
                            : -1;

                        progressContainerEnd = cEnd > cStart ? cEnd : progressScopeEnd;
                        if (cEnd > cStart)
                        {
                            var raw = html.Substring(cStart, cEnd - cStart);
                            progressInner = DecodeIfNeeded(StripTags(raw));
                        }
                    }
                    else
                    {
                        progressContainerEnd = progressScopeEnd;
                    }
                }
            }

            int? minute = null;
            string minuteStatus = string.Empty;
            int? aggHomeScore = null;
            int? aggAwayScore = null;

            // Aggregate score only when the marker text is present (avoid regex on every card).
            if (hasProgressContainer && containerIdx >= 0)
            {
                int aggSearchEnd = progressContainerEnd > containerIdx ? progressContainerEnd : Math.Min(containerIdx + 2000, end);
                int aggSearchLen = aggSearchEnd - containerIdx;
                if (aggSearchLen > 0 && aggSearchLen < 5000)
                {
                    var aggIdx = html.IndexOf("(Agg", containerIdx, aggSearchLen, StringComparison.OrdinalIgnoreCase);
                    if (aggIdx >= 0)
                    {
                        TryParseAggScores(html, aggIdx, Math.Min(aggIdx + 32, end), out aggHomeScore, out aggAwayScore);
                    }
                    else if (html.IndexOf("Aggregate", containerIdx, aggSearchLen, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var aggBlock = html.Substring(containerIdx, aggSearchLen);
                        var aggMatch2 = AggScoreAltRegex.Match(aggBlock);
                        if (aggMatch2.Success)
                        {
                            aggHomeScore = TryParseInt(aggMatch2.Groups["h"].Value);
                            aggAwayScore = TryParseInt(aggMatch2.Groups["a"].Value);
                        }
                    }
                }
            }

            // Skip minute regex work for terminal FT/HT cards.
            if (!quickFt && !quickHt && !string.IsNullOrEmpty(progressInner))
            {
                if (progressInner.IndexOf('+') >= 0)
                {
                    var injPlus = InjuryTimeApostropheRegex.Match(progressInner);
                    if (!injPlus.Success)
                    {
                        injPlus = InjuryTimePlainRegex.Match(progressInner);
                    }

                    if (injPlus.Success)
                    {
                        var m = TryParseInt(injPlus.Groups[1].Value) ?? 0;
                        var e = TryParseInt(injPlus.Groups[2].Value) ?? 0;
                        minute = m * 100 + e;
                        minuteStatus = $"{m}+{e}'";
                    }
                }

                if (!minute.HasValue)
                {
                    var mMatch = MinuteApostropheRegex.Match(progressInner);
                    if (!mMatch.Success) mMatch = MinuteWordsRegex.Match(progressInner);
                    if (mMatch.Success) minute = TryParseInt(mMatch.Groups["m"].Value);
                    if (minute.HasValue) minuteStatus = $"{minute}'";
                }
            }

            if (!quickFt && !quickHt && !minute.HasValue && hasProgressContainer && containerIdx >= 0 && containerIdx < end)
            {
                int minSearchEnd = Math.Min(containerIdx + 2000, end);
                int minSearchLen = minSearchEnd - containerIdx;
                if (minSearchLen > 0)
                {
                    if (html.IndexOf('+', containerIdx, minSearchLen) >= 0)
                    {
                        var containerBlock = html.Substring(containerIdx, minSearchLen);
                        var injMatch = InjuryTimeApostropheRegex.Match(containerBlock);
                        if (!injMatch.Success)
                        {
                            injMatch = InjuryTimePlainRegex.Match(containerBlock);
                        }

                        if (injMatch.Success)
                        {
                            var m = TryParseInt(injMatch.Groups[1].Value) ?? 0;
                            var e = TryParseInt(injMatch.Groups[2].Value) ?? 0;
                            minute = m * 100 + e;
                            minuteStatus = $"{m}+{e}'";
                        }
                    }

                    if (!minute.HasValue)
                    {
                        int styledIdx = html.IndexOf("StyledPeriod", containerIdx, minSearchLen, StringComparison.Ordinal);
                        if (styledIdx >= containerIdx)
                        {
                            int periodEnd = Math.Min(styledIdx + 200, html.Length);
                            var periodBlock = html.Substring(styledIdx, periodEnd - styledIdx);
                            var minMatch = StyledPeriodMinuteRegex.Match(periodBlock);
                            if (minMatch.Success)
                            {
                                minute = TryParseInt(minMatch.Groups[1].Value);
                                if (minute.HasValue) minuteStatus = $"{minute}'";
                            }
                        }
                    }
                }
            }

            var hasScores = homeScore.HasValue || awayScore.HasValue;

            bool ProgressContains(string value) =>
                !string.IsNullOrEmpty(progressInner)
                && progressInner.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

            bool ContainerContains(string value)
            {
                if (!hasProgressContainer || containerIdx < 0) return false;
                var searchLen = Math.Min(ProgressProbeMaxChars, end - containerIdx);
                return searchLen > 0
                       && html.IndexOf(value, containerIdx, searchLen, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            var hasFullTime = hasProgressContainer && (quickFt ||
                progressInner.Equals("FT", StringComparison.OrdinalIgnoreCase) ||
                ProgressContains("Full time") ||
                ProgressContains("FT") ||
                ContainerContains(">FT<") ||
                ContainerContains("Full time"));
            var hasHalfTime = hasProgressContainer && (quickHt ||
                progressInner.Equals("HT", StringComparison.OrdinalIgnoreCase) ||
                ProgressContains("Half time") ||
                ProgressContains("HT") ||
                ContainerContains(">HT<") ||
                ContainerContains("Half time"));

            var hasExplicitInProgress = hasProgressContainer && ProgressContains("in progress");

            var hasLiveKeyword = hasProgressContainer &&
                progressInner.Trim().Equals("Live", StringComparison.OrdinalIgnoreCase);

            var hasLive = hasProgressContainer && (hasExplicitInProgress || hasLiveKeyword);

            var isPostponed = hasProgressContainer && (ProgressContains("Postponed") || ContainerContains("Postponed"));

            var hasProgress = hasProgressContainer && (hasFullTime || hasHalfTime || minute.HasValue || hasLive || hasScores);
            if (isPostponed) { minute = null; hasProgress = false; }

            var isHalf = hasProgress && hasHalfTime;
            var isFinished = hasProgress && hasFullTime;
            var isInProgress = hasProgress && !isFinished && (isHalf || minute.HasValue || hasLive);
            if (minute.HasValue) { isFinished = false; isHalf = false; isInProgress = true; }

            string status;
            if (isPostponed) status = "Postponed";
            else if (isFinished) status = "FT";
            else if (isHalf) status = "HT";
            else if (minute.HasValue) status = minuteStatus;
            else if (isInProgress) status = "Live";
            else status = string.Empty;

            // Badges via badge-img src= (avoids scanning whole card for every .svg/.webp)
            ExtractBadges(html, start, end, out var homeBadge, out var awayBadge);

            if (string.IsNullOrEmpty(homeBadge) || string.IsNullOrEmpty(awayBadge) ||
                homeBadge.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
                awayBadge.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
            {
                homeBadge = string.Empty;
                awayBadge = string.Empty;
            }

            bool afterExtraTime = false;
            bool inExtraTime = false;
            string penaltyWinner = string.Empty;
            int? penaltyWinnerGoals = null;
            int? penaltyLoserGoals = null;

            // Special-state probes only when the card might contain them.
            var maybeSpecial = !quickFt && !quickHt && hasProgressContainer && (
                ContainerContains("penalt") ||
                ContainerContains("extra time") ||
                ContainerContains("AET") ||
                ContainerContains(">ET<") ||
                ProgressContains("ET") ||
                ProgressContains("extra time") ||
                ProgressContains("penalt"));

            if (maybeSpecial || (!quickFt && !quickHt && rangeLen > 0 &&
                (html.IndexOf("penalties", start, rangeLen, StringComparison.OrdinalIgnoreCase) >= 0
                 || html.IndexOf("After extra time", start, rangeLen, StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                int aetIdx = html.IndexOf("After extra time", start, rangeLen, StringComparison.OrdinalIgnoreCase);
                if (aetIdx < 0) aetIdx = html.IndexOf(">AET<", start, rangeLen, StringComparison.OrdinalIgnoreCase);
                if (aetIdx < 0) aetIdx = html.IndexOf(">AET</", start, rangeLen, StringComparison.OrdinalIgnoreCase);
                afterExtraTime = aetIdx >= 0;

                inExtraTime = hasProgressContainer && !afterExtraTime && (ProgressContains("ET") || ProgressContains("extra time") || ContainerContains(">ET<"));

                int penIdx = html.IndexOf("penalties", start, rangeLen, StringComparison.OrdinalIgnoreCase);
                if (penIdx >= 0)
                {
                    int cappedRangeLen = Math.Min(rangeLen, 5000);
                    var blockSub = html.Substring(start, cappedRangeLen);
                    var penMatch = PenaltiesWinRegex.Match(blockSub);
                    if (penMatch.Success)
                    {
                        penaltyWinner = DecodeIfNeeded(penMatch.Groups["winner"].Value.Trim());
                        penaltyWinnerGoals = TryParseInt(penMatch.Groups["w"].Value);
                        penaltyLoserGoals = TryParseInt(penMatch.Groups["l"].Value);

                        isFinished = true;
                        isInProgress = false;
                        isHalf = false;
                        hasProgress = true;
                        status = "FT";
                    }
                    else if (ProgressContains("penalties") || ContainerContains("penalties"))
                    {
                        isFinished = false;
                        isInProgress = true;
                        status = "Penalties";
                        minute = null;
                    }
                }
            }

            var isPenalties = status == "Penalties" || (hasProgressContainer && ProgressContains("penalties"));
            if (isPenalties && string.IsNullOrEmpty(penaltyWinner))
            {
                isFinished = false;
                isInProgress = true;
                status = "Penalties";
                minute = null;
            }

            if (eventStatusMap.TryGetValue(id, out var statusMap))
            {
                // If we haven't found a minute yet, try to parse it from periodLabel
                if (!minute.HasValue && !string.IsNullOrEmpty(statusMap.periodLabel))
                {
                    var periodText = statusMap.periodLabel;
                    // Try to parse injury time format: "90+8'" or "90+8"
                    var injMatch = InjuryTimePlainRegex.Match(periodText);
                    if (injMatch.Success)
                    {
                        var m = TryParseInt(injMatch.Groups[1].Value) ?? 0;
                        var e = TryParseInt(injMatch.Groups[2].Value) ?? 0;
                        minute = m * 100 + e;
                        minuteStatus = $"{m}+{e}'";
                        isInProgress = true;
                        hasProgress = true;
                    }
                    else
                    {
                        // Try to parse simple minute format: "68'" or "68 minutes"
                        var minMatch = MinuteApostropheRegex.Match(periodText);
                        if (!minMatch.Success)
                        {
                            minMatch = MinuteWordsRegex.Match(periodText);
                        }
                        if (minMatch.Success)
                        {
                            minute = TryParseInt(minMatch.Groups[1].Value);
                            if (minute.HasValue)
                            {
                                minuteStatus = $"{minute}'";
                                isInProgress = true;
                                hasProgress = true;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(status))
                {
                    // BBC lifecycle enums (PreEvent/MidEvent/PostEvent) are not display statuses.
                    // Only promote human-readable map statuses such as Postponed.
                    var mapStatus = statusMap.status ?? string.Empty;
                    if (mapStatus.IndexOf("Postponed", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        status = "Postponed";
                        isPostponed = true;
                        hasProgress = false;
                        isFinished = false;
                        isInProgress = false;
                        isHalf = false;
                        minute = null;
                    }
                    else if (OrdinalStatusRegex.IsMatch(mapStatus))
                    {
                        status = mapStatus;
                        minute = 0;
                    }
                }
            }

            // Fix precedence: If ET is detected, don't just show minute
            if (inExtraTime)
            {
                status = "ET";
                // If minute has value, we can keep it in 'minute' field but status text might need care.
                // BbcFixture.Status is string.
            }
            else if (afterExtraTime)
            {
                isFinished = true;
                isInProgress = false;
                status = "AET";
                minute = null;
            }
            else if (status != "Penalties")
            {
                if (isPostponed) status = "Postponed";
                else if (isFinished) status = "FT";
                else if (isHalf) status = "HT";
                else if (minute.HasValue) status = minuteStatus;
                else if (isInProgress) status = "Live";
                // else string.Empty loop init
            }

            var kickoffUtc = eventKickoffMap.TryGetValue(id, out var mappedKickoff)
                ? mappedKickoff
                : DateTime.MinValue;

            list.Add(new BbcFixture(home, away, kickoffUtc, status, isFinished, isInProgress, isHalf, minute, homeScore, awayScore, homeBadge, awayBadge, currentLeague, hasProgress, afterExtraTime, penaltyWinner, penaltyWinnerGoals, penaltyLoserGoals, aggHomeScore, aggAwayScore));
        }
    }

    private static void ExtractBadges(string html, int start, int end, out string homeBadge, out string awayBadge)
    {
        homeBadge = string.Empty;
        awayBadge = string.Empty;
        var rangeLen = end - start;
        if (rangeLen <= 0) return;

        var pos = start;
        for (var i = 0; i < 2; i++)
        {
            var badgeImg = html.IndexOf("badge-img", pos, end - pos, StringComparison.OrdinalIgnoreCase);
            if (badgeImg < 0 || badgeImg >= end) break;

            var srcIdx = html.IndexOf("src=\"", badgeImg, end - badgeImg, StringComparison.OrdinalIgnoreCase);
            if (srcIdx < 0 || srcIdx >= end)
            {
                pos = badgeImg + 9;
                continue;
            }

            var urlStart = srcIdx + 5;
            var urlEnd = html.IndexOf('"', urlStart, end - urlStart);
            if (urlEnd <= urlStart)
            {
                pos = badgeImg + 9;
                continue;
            }

            var url = html.Substring(urlStart, urlEnd - urlStart);
            if (i == 0) homeBadge = url;
            else awayBadge = url;
            pos = urlEnd + 1;
        }

        // Fallback for markup that has badge URLs without badge-img test ids.
        if (string.IsNullOrEmpty(homeBadge) || string.IsNullOrEmpty(awayBadge))
        {
            ExtractBadgesByExtension(html, start, end, out homeBadge, out awayBadge);
        }
    }

    private static void ExtractBadgesByExtension(string html, int start, int end, out string homeBadge, out string awayBadge)
    {
        homeBadge = string.Empty;
        awayBadge = string.Empty;

        int FindBadgeExtension(int searchStart, int searchEnd)
        {
            if (searchStart >= searchEnd) return -1;
            int svgIdx = html.IndexOf(".svg", searchStart, searchEnd - searchStart, StringComparison.OrdinalIgnoreCase);
            int webpIdx = html.IndexOf(".webp", searchStart, searchEnd - searchStart, StringComparison.OrdinalIgnoreCase);
            if (svgIdx >= 0 && webpIdx >= 0) return Math.Min(svgIdx, webpIdx);
            if (svgIdx >= 0) return svgIdx;
            if (webpIdx >= 0) return webpIdx;
            return -1;
        }

        int GetExtensionLength(int extIdx)
        {
            if (extIdx + 5 <= html.Length
                && html.AsSpan(extIdx, 5).Equals(".webp", StringComparison.OrdinalIgnoreCase))
                return 5;
            return 4;
        }

        int firstBadgeIdx = FindBadgeExtension(start, end);
        if (firstBadgeIdx < 0) return;

        const int badgeHttpLookback = 200;
        int searchBackLimit = Math.Max(start, firstBadgeIdx - badgeHttpLookback);
        int httpIdx = html.LastIndexOf("http", firstBadgeIdx, firstBadgeIdx - searchBackLimit, StringComparison.OrdinalIgnoreCase);
        if (httpIdx < 0) return;

        int extLen = GetExtensionLength(firstBadgeIdx);
        homeBadge = html.Substring(httpIdx, firstBadgeIdx - httpIdx + extLen);

        int searchStart2 = firstBadgeIdx + extLen;
        if (searchStart2 >= end) return;

        int secondBadgeIdx = FindBadgeExtension(searchStart2, end);
        if (secondBadgeIdx < 0) return;

        int searchBackLimit2 = Math.Max(start, secondBadgeIdx - badgeHttpLookback);
        int http2Idx = html.LastIndexOf("http", secondBadgeIdx, secondBadgeIdx - searchBackLimit2, StringComparison.OrdinalIgnoreCase);
        if (http2Idx < 0) return;

        int extLen2 = GetExtensionLength(secondBadgeIdx);
        awayBadge = html.Substring(http2Idx, secondBadgeIdx - http2Idx + extLen2);
    }

    private static int MinNonNegative(int a, int b)
    {
        if (a < 0) return b;
        if (b < 0) return a;
        return Math.Min(a, b);
    }

    private static bool TryParseAggScores(string html, int start, int end, out int? home, out int? away)
    {
        home = null;
        away = null;
        // Expected shape near start: (Agg 4-2)
        var dash = html.IndexOf('-', start, Math.Max(0, end - start));
        if (dash < 0) return false;

        var hEnd = dash;
        while (hEnd > start && char.IsWhiteSpace(html[hEnd - 1])) hEnd--;
        var hStart = hEnd;
        while (hStart > start && char.IsDigit(html[hStart - 1])) hStart--;
        if (hStart >= hEnd) return false;

        var aStart = dash + 1;
        while (aStart < end && char.IsWhiteSpace(html[aStart])) aStart++;
        var aEnd = aStart;
        while (aEnd < end && char.IsDigit(html[aEnd])) aEnd++;
        if (aEnd <= aStart) return false;

        if (!int.TryParse(html.AsSpan(hStart, hEnd - hStart), out var h)) return false;
        if (!int.TryParse(html.AsSpan(aStart, aEnd - aStart), out var a)) return false;
        home = h;
        away = a;
        return true;
    }

    private static string DecodeIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.IndexOf('&') >= 0 ? System.Net.WebUtility.HtmlDecode(value) : value;
    }

    private static int? TryParseInt(string? text)
    {
        if (int.TryParse(text, out var v)) return v;
        return null;
    }

    private static string StripTags(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var array = new char[input.Length];
        int arrayIndex = 0;
        bool inside = false;

        for (int i = 0; i < input.Length; i++)
        {
            char let = input[i];
            if (let == '<')
            {
                inside = true;
                continue;
            }
            if (let == '>')
            {
                inside = false;
                continue;
            }
            if (!inside)
            {
                array[arrayIndex] = let;
                arrayIndex++;
            }
        }
        return new string(array, 0, arrayIndex).Trim();
    }
}
