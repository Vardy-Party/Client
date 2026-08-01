using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VardyParty.Models;

namespace VardyParty.Parsers;

public class BbcHtmlParser(ILogger<BbcHtmlParser> logger, IBbcJsonParser bbcJsonParser) : IBbcHtmlParser
{
    private static readonly int SlowGameWarnMs = 50;
    // Real BBC event cards are ~2.5–4KB apart; cap avoids scanning hundreds of KB of trailing scripts.
    private const int MaxGameBlockChars = 8192;
    private const string EscapedStartDateTimeMarker = "\\\"startDateTime\\\":\\\"";
    private const string EscapedIdMarker = "\\\"id\\\":\\\"";
    private const string UnescapedStartDateTimeMarker = "\"startDateTime\":\"";
    private const string UnescapedIdMarker = "\"id\":\"";
    private const string DataEventIdMarker = "data-event-id=";
    private const string H2Marker = "<h2";

    private static readonly Regex AggScoreRegex = new(@"\(Agg\s+(?<h>\d+)\s*-\s*(?<a>\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
        var list = new List<BbcFixture>();
        if (string.IsNullOrEmpty(html)) return list;

        // Stream-scan initial page JSON to build event status map (low memory)
        var swMap = System.Diagnostics.Stopwatch.StartNew();
        var eventStatusMap = bbcJsonParser.BuildEventStatusMapStreaming(html, cancellationToken);
        swMap.Stop();
        
        cancellationToken.ThrowIfCancellationRequested();

        if (eventStatusMap.Count == 0)
        {
            logger.LogWarning("[BBC] Event status map empty after streaming parse; continuing with HTML-only status");
        }

        var swKickoff = System.Diagnostics.Stopwatch.StartNew();
        var eventKickoffMap = BuildEventKickoffMap(html);
        swKickoff.Stop();

        var swSerial = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ScanHtmlSerial(html, list, eventStatusMap, eventKickoffMap);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BBC] Serial scan failed");
        }
        swSerial.Stop();

        sw.Stop();
        
        logger.LogInformation(
            "[BBC] Parsing stats: MapStream={Map}ms StatusMap={StatusCount} KickoffMap={Kickoff}ms SerialScan={Serial}ms Fixtures={FixtureCount} Total={Total}ms",
            swMap.ElapsedMilliseconds,
            eventStatusMap.Count,
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
                        && TryParseKickoffUtc(dtText, out var kickoffUtc))
                    {
                        map[id] = kickoffUtc;
                    }
                }
            }

            pos = dtEnd + 1;
        }
    }

    private static bool TryParseKickoffUtc(string? value, out DateTime kickoffUtc)
    {
        kickoffUtc = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        kickoffUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        return true;
    }

    private void ScanHtmlSerial(
        string html,
        List<BbcFixture> list,
        Dictionary<string, (string periodLabel, string status, string statusComment)> eventStatusMap,
        Dictionary<string, DateTime> eventKickoffMap)
    {
        // Single pass scanner over "<h2" / "data-event-id=" markers.
        int cursor = 0;
        string currentLeague = string.Empty;
        int len = html.Length;

        while (cursor < len)
        {
            int nextH2 = html.IndexOf(H2Marker, cursor, StringComparison.OrdinalIgnoreCase);
            int nextGame = html.IndexOf(DataEventIdMarker, cursor, StringComparison.Ordinal);

            if (nextH2 < 0 && nextGame < 0) break;

            bool isHeader;
            int foundIdx;
            if (nextH2 >= 0 && nextGame >= 0)
            {
                isHeader = nextH2 < nextGame;
                foundIdx = isHeader ? nextH2 : nextGame;
            }
            else if (nextH2 >= 0)
            {
                isHeader = true;
                foundIdx = nextH2;
            }
            else
            {
                isHeader = false;
                foundIdx = nextGame;
            }

            cursor = foundIdx;

            if (isHeader)
            {
                int endH2 = html.IndexOf("</h2>", cursor, StringComparison.OrdinalIgnoreCase);
                if (endH2 > cursor)
                {
                    int closeTag = html.IndexOf('>', cursor);
                    if (closeTag > cursor && closeTag < endH2)
                    {
                        var content = html.Substring(closeTag + 1, endH2 - closeTag - 1);
                        var text = System.Net.WebUtility.HtmlDecode(StripTags(content));
                        if (!string.IsNullOrWhiteSpace(text) && !text.Contains("Scores & Fixtures", StringComparison.OrdinalIgnoreCase))
                        {
                            currentLeague = text;
                        }
                    }
                    cursor = endH2 + 5;
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
                int nextMarker2 = html.IndexOf(H2Marker, searchFrom, StringComparison.OrdinalIgnoreCase);
                int limit = cappedLimit;
                if (nextMarker1 >= 0 && nextMarker1 < limit) limit = nextMarker1;
                if (nextMarker2 >= 0 && nextMarker2 < limit) limit = nextMarker2;

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
                    var swGame = System.Diagnostics.Stopwatch.StartNew();
                    ParseGameNative(html, cursor, limit, id, currentLeague, list, eventStatusMap, eventKickoffMap);
                    swGame.Stop();
                    if (swGame.ElapsedMilliseconds > SlowGameWarnMs)
                    {
                        logger.LogWarning("[BBC] Slow game parse ({Elapsed}ms) for ID {Id} in League {League}", swGame.ElapsedMilliseconds, id, currentLeague);
                    }
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
        // Extract fields using IndexOf within range
        // Helper to find subs
        string ExtractRange(string marker)
        {
            int rangeLen = end - start;
            if (rangeLen <= 0) return string.Empty;
            int mIdx = html.IndexOf(marker, start, rangeLen, StringComparison.OrdinalIgnoreCase);
            if (mIdx < 0) return string.Empty;
            
            // "DesktopValue"
            int dIdx = html.IndexOf("DesktopValue", mIdx, end - mIdx, StringComparison.OrdinalIgnoreCase);
            if (dIdx < 0) return string.Empty;

            int valStart = html.IndexOf('>', dIdx, end - dIdx);
            if (valStart < 0) return string.Empty;
            valStart++; // skip >

            if (valStart >= end) return string.Empty;

            int valEnd = html.IndexOf('<', valStart, end - valStart);
            if (valEnd < 0) return string.Empty;

            return html.Substring(valStart, valEnd - valStart);
        }

        var homeRaw = ExtractRange("WithInlineFallback-TeamHome");
        var awayRaw = ExtractRange("WithInlineFallback-TeamAway");
        var home = System.Net.WebUtility.HtmlDecode(homeRaw);
        var away = System.Net.WebUtility.HtmlDecode(awayRaw);

        if (string.IsNullOrWhiteSpace(home) || string.IsNullOrWhiteSpace(away))
        {
             // Fallback DesktopValue scan (legacy)
             // simplified: if we can't find team names, skip
             return;
        }

        string ExtractScore(string marker)
        {
             // e.g. HomeScore... >3<
             int rangeLen = end - start;
             if (rangeLen <= 0) return string.Empty;
 
             int mIdx = html.IndexOf(marker, start, rangeLen, StringComparison.OrdinalIgnoreCase);
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
            int containerIdx = html.IndexOf("MatchProgressContainer", start, rangeLen, StringComparison.OrdinalIgnoreCase);
            bool hasProgressContainer = false;
            int progressContainerEnd = -1;
            if (containerIdx >= 0)
            {
                hasProgressContainer = true;
                // Find the closing > of the container opening tag
                int cStart = html.IndexOf('>', containerIdx) + 1;
                if (cStart > 0)
                {
                    // Search for the closing </div> - try bounded first, then fallback with larger limit
                    int searchLimit = Math.Min(end - cStart, 5000);  // Reasonable limit for container content
                    int cEnd = html.IndexOf("</div>", cStart, searchLimit, StringComparison.OrdinalIgnoreCase);
                    
                    // If not found in limited range, search further but cap at 10KB to prevent performance issues
                    // This handles edge cases while avoiding the 300ms+ penalty for games at end of large pages
                    if (cEnd < 0)
                    {
                        int maxSearch = Math.Min(html.Length - cStart, 10000);
                        cEnd = html.IndexOf("</div>", cStart, maxSearch, StringComparison.OrdinalIgnoreCase);
                    }
                    
                    progressContainerEnd = cEnd;
                    if (cEnd > cStart)
                    {
                        var raw = html.Substring(cStart, cEnd - cStart);
                        // Decode HTML entities (e.g. &#39; for apostrophe) before parsing
                        progressInner = System.Net.WebUtility.HtmlDecode(StripTags(raw));
                    }
                }
            }
        
            int? minute = null;
            string minuteStatus = string.Empty;
            int? aggHomeScore = null;
            int? aggAwayScore = null;
            
            // Extract aggregate score if present - search in raw HTML near progress container
            if (hasProgressContainer && containerIdx >= 0)
            {
                int aggSearchEnd = progressContainerEnd > containerIdx ? progressContainerEnd : Math.Min(containerIdx + 2000, end);
                int aggSearchLen = aggSearchEnd - containerIdx;
                if (aggSearchLen > 0 && aggSearchLen < 5000)
                {
                    var aggBlock = html.Substring(containerIdx, aggSearchLen);
                    var aggMatch = AggScoreRegex.Match(aggBlock);
                    if (aggMatch.Success)
                    {
                        aggHomeScore = TryParseInt(aggMatch.Groups["h"].Value);
                        aggAwayScore = TryParseInt(aggMatch.Groups["a"].Value);
                    }
                    else
                    {
                        var aggMatch2 = AggScoreAltRegex.Match(aggBlock);
                        if (aggMatch2.Success)
                        {
                            aggHomeScore = TryParseInt(aggMatch2.Groups["h"].Value);
                            aggAwayScore = TryParseInt(aggMatch2.Groups["a"].Value);
                        }
                    }
                }
            }
            
            if (!string.IsNullOrEmpty(progressInner))
            {
                // First try: match injury time pattern with apostrophe before plus: 90'+8
                var injPlus = InjuryTimeApostropheRegex.Match(progressInner);

                // If no apostrophe variant, try without apostrophe: 90+8
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
                else
                {
                    var mMatch = MinuteApostropheRegex.Match(progressInner);
                    if (!mMatch.Success) mMatch = MinuteWordsRegex.Match(progressInner);
                    if (mMatch.Success) minute = TryParseInt(mMatch.Groups["m"].Value);
                    if (minute.HasValue) minuteStatus = $"{minute}'";
                }
            }
            
            // If minute not found in progressInner, search raw HTML for nested div patterns
            // Handles: <div class="StyledPeriod"><div>69&#x27;</div></div>
            if (!minute.HasValue && hasProgressContainer && containerIdx >= 0 && containerIdx < end)
            {
                int minSearchEnd = Math.Min(containerIdx + 2000, end);
                int minSearchLen = minSearchEnd - containerIdx;
                if (minSearchLen > 0)
                {
                    // First, try to find injury time pattern in the container block
                    int containerBlockLen = minSearchLen;
                    var containerBlock = html.Substring(containerIdx, containerBlockLen);
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
                    else
                    {
                        // Fallback to StyledPeriod single-digit pattern
                        int styledIdx = html.IndexOf("StyledPeriod", containerIdx, minSearchLen, StringComparison.OrdinalIgnoreCase);
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

            bool RangeContains(string value)
            {
                if (!hasProgressContainer || containerIdx < 0) return false;
                var searchStart = containerIdx;
                // If we found the container end, search up to there; otherwise search a reasonable distance
                var searchEnd = progressContainerEnd > 0 ? progressContainerEnd + 100 : Math.Min(containerIdx + 5000, html.Length);
                var searchLength = searchEnd - searchStart;
                
                // Bounds check to avoid negative lengths
                if (searchLength <= 0) return false;
                
                try
                {
                    return html.IndexOf(value, searchStart, searchLength, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch
                {
                    // Fallback to bounded search with cap at 10KB to prevent performance issues
                    var maxFallback = Math.Min(html.Length - searchStart, 10000);
                    if (maxFallback > 0)
                    {
                        return html.IndexOf(value, searchStart, maxFallback, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    return false;
                }
            }

            // Check for FT status in multiple ways
            var hasFullTime = hasProgressContainer && (
                progressInner.Equals("FT", StringComparison.OrdinalIgnoreCase) ||
                progressInner.IndexOf("Full time", StringComparison.OrdinalIgnoreCase) >= 0 ||
                progressInner.IndexOf("FT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RangeContains(">FT<") ||
                RangeContains("Full time") ||
                RangeContains("<div>FT</div>") ||
                RangeContains("FT</div>"));  // Handle FT that might be in a div
            var hasHalfTime = hasProgressContainer && (
                progressInner.Equals("HT", StringComparison.OrdinalIgnoreCase) ||
                progressInner.IndexOf("Half time", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RangeContains(">HT<") ||
                RangeContains("Half time"));

            var hasExplicitInProgress = hasProgressContainer &&
                progressInner.IndexOf("in progress", StringComparison.OrdinalIgnoreCase) >= 0;

            // Treat as live only when the period text itself is exactly "Live".
            var hasLiveKeyword = hasProgressContainer &&
                progressInner.Trim().Equals("Live", StringComparison.OrdinalIgnoreCase);

            var hasLive = hasProgressContainer && (hasExplicitInProgress || hasLiveKeyword);

            var isPostponed = hasProgressContainer && progressInner.IndexOf("Postponed", StringComparison.OrdinalIgnoreCase) >= 0;

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

            // Badges
            string homeBadge = string.Empty;
            string awayBadge = string.Empty;
            
            // Helper to find badge URL by searching for image extensions
            int FindBadgeExtension(int searchStart, int searchEnd)
            {
                if (searchStart >= searchEnd) return -1;
                
                int svgIdx = html.IndexOf(".svg", searchStart, searchEnd - searchStart, StringComparison.OrdinalIgnoreCase);
                int webpIdx = html.IndexOf(".webp", searchStart, searchEnd - searchStart, StringComparison.OrdinalIgnoreCase);
                
                // Return the first occurrence (whichever comes first, or -1 if neither found)
                if (svgIdx >= 0 && webpIdx >= 0) return Math.Min(svgIdx, webpIdx);
                if (svgIdx >= 0) return svgIdx;
                if (webpIdx >= 0) return webpIdx;
                return -1;
            }
            
            int GetExtensionLength(int extIdx)
            {
                // Check if it's .svg or .webp
                if (extIdx + 4 <= html.Length && html.Substring(extIdx, 4).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                    return 4;
                if (extIdx + 5 <= html.Length && html.Substring(extIdx, 5).Equals(".webp", StringComparison.OrdinalIgnoreCase))
                    return 5;
                return 4; // default fallback
            }
            
            int firstBadgeIdx = FindBadgeExtension(start, end);
            if (firstBadgeIdx >= 0)
            {
                // Scan back for http
                const int badgeHttpLookback = 200;
                int searchBackLimit = Math.Max(start, firstBadgeIdx - badgeHttpLookback);
                int httpIdx = html.LastIndexOf("http", firstBadgeIdx, firstBadgeIdx - searchBackLimit, StringComparison.OrdinalIgnoreCase);
                if (httpIdx >= 0)
                {
                    int extLen = GetExtensionLength(firstBadgeIdx);
                    homeBadge = html.Substring(httpIdx, firstBadgeIdx - httpIdx + extLen);
                    
                    // second badge
                    int searchStart2 = firstBadgeIdx + extLen;
                    if (searchStart2 < end)
                    {
                        int secondBadgeIdx = FindBadgeExtension(searchStart2, end);
                        if (secondBadgeIdx >= 0)
                        {
                             int searchBackLimit2 = Math.Max(start, secondBadgeIdx - badgeHttpLookback);
                             int http2Idx = html.LastIndexOf("http", secondBadgeIdx, secondBadgeIdx - searchBackLimit2, StringComparison.OrdinalIgnoreCase);
                             if (http2Idx >= 0)
                             {
                                 int extLen2 = GetExtensionLength(secondBadgeIdx);
                                 awayBadge = html.Substring(http2Idx, secondBadgeIdx - http2Idx + extLen2);
                             }
                        }
                    }
                }
            }

            // Apply placeholder logic here: If either badge is missing or a placeholder, treat both as empty
            if (string.IsNullOrEmpty(homeBadge) || string.IsNullOrEmpty(awayBadge) || 
                homeBadge.Contains("placeholder", StringComparison.OrdinalIgnoreCase) || 
                awayBadge.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
            {
                homeBadge = string.Empty;
                awayBadge = string.Empty;
            }

            // Optimization: replace regex with IndexOf
            // Check "After extra time" in the full block range
            int aetIdx = html.IndexOf("After extra time", start, rangeLen, StringComparison.OrdinalIgnoreCase);
            if (aetIdx < 0) aetIdx = html.IndexOf("AET", start, rangeLen, StringComparison.OrdinalIgnoreCase);
            bool afterExtraTime = (aetIdx >= 0);

            // Detect in-progress extra time
            var inExtraTime = hasProgressContainer && !afterExtraTime && (progressInner.IndexOf("ET", StringComparison.OrdinalIgnoreCase) >= 0 || progressInner.IndexOf("extra time", StringComparison.OrdinalIgnoreCase) >= 0);

            string penaltyWinner = string.Empty;
            int? penaltyWinnerGoals = null;
            int? penaltyLoserGoals = null;

            if (start < end) // valid range check
            {
                 int penIdx = html.IndexOf("penalties", start, rangeLen, StringComparison.OrdinalIgnoreCase);
                 if (penIdx >= 0)
                 {
                     // Cap the range for penalty regex to prevent performance issues with games at end of page
                     // Last game on page could have rangeLen of 100KB+, causing 400ms+ regex penalty
                     int cappedRangeLen = Math.Min(rangeLen, 5000);
                     var blockSub = html.Substring(start, cappedRangeLen);
                     var penMatch = PenaltiesWinRegex.Match(blockSub);
                     if (penMatch.Success)
                     {
                         penaltyWinner = System.Net.WebUtility.HtmlDecode(penMatch.Groups["winner"].Value.Trim());
                         penaltyWinnerGoals = TryParseInt(penMatch.Groups["w"].Value);
                         penaltyLoserGoals = TryParseInt(penMatch.Groups["l"].Value);

                         isFinished = true;
                         isInProgress = false;
                         isHalf = false;
                         hasProgress = true;
                         status = "FT";
                     }
                     else if (penIdx >= 0) // Penalties mentioned but not a win result (in progress)
                     {
                         // Check if progressInner contains Penalties
                         if (progressInner.IndexOf("penalties", StringComparison.OrdinalIgnoreCase) >= 0)
                         {
                             // In progress penalties
                             isFinished = false;
                             isInProgress = true;
                             status = "Penalties";
                             minute = null; // minute not relevant
                         }
                     }
                 }
            }

            var isPenalties = status == "Penalties" || (hasProgressContainer && progressInner.IndexOf("penalties", StringComparison.OrdinalIgnoreCase) >= 0); 
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
