using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using VardyParty.Models;

namespace VardyParty.Parsers;

public class BbcHtmlParser(ILogger<BbcHtmlParser> logger, IBbcJsonParser bbcJsonParser) : IBbcHtmlParser
{
    private static readonly int Timeout = 200;

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

        // Fallback: extract the JSON object following __INITIAL_DATA__ and scan it for id/status pairs
        if (eventStatusMap.Count == 0)
        {
            var swFallback = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                int anchorIdx = -1;
                var anchorNames = new[] { "window.__INITIAL_DATA__", "__INITIAL_DATA__" };
                foreach (var a in anchorNames)
                {
                    anchorIdx = html.IndexOf(a, StringComparison.OrdinalIgnoreCase);
                    if (anchorIdx >= 0) break;
                }

                if (anchorIdx >= 0)
                {
                    int braceStart = html.IndexOf('{', anchorIdx);
                    if (braceStart >= 0)
                    {
                        bool inString = false;
                        int depth = 0;
                        int blobEnd = -1;
                        for (int i = braceStart; i < html.Length; i++)
                        {
                            var c = html[i];
                            if (c == '"')
                            {
                                int back = i - 1; bool esc = false; while (back >= 0 && html[back] == '\\') { esc = !esc; back--; }
                                if (!esc) inString = !inString;
                            }
                            if (!inString)
                            {
                                if (c == '{') depth++;
                                else if (c == '}')
                                {
                                    depth--;
                                    if (depth == 0)
                                    {
                                        blobEnd = i;
                                        break;
                                    }
                                }
                            }
                        }

                        if (blobEnd > braceStart)
                        {
                            var jsonBlob = html.Substring(braceStart, blobEnd - braceStart + 1);
                            var blobMatches = Regex.Matches(jsonBlob, "\"id\"\\s*:\\s*\"(?<id>s-[^\"']+)\"[\\s\\S]*?\"status\"\\s*:\\s*\"(?<s>[^\"']+)\"", RegexOptions.IgnoreCase);
                            foreach (Match m in blobMatches)
                            {
                                try
                                {
                                    var id = m.Groups["id"].Value;
                                    var s = m.Groups["s"].Value;
                                    if (!string.IsNullOrEmpty(id) && !eventStatusMap.ContainsKey(id)) eventStatusMap[id] = (string.Empty, s, string.Empty);
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("[BBC] JSON fallback parse error: {Message}", ex.Message);
            }
            swFallback.Stop();
            logger.LogInformation("[BBC] Fallback map build took {Elapsed}ms", swFallback.ElapsedMilliseconds);
        }

        // Final fallback: scan entire HTML for id/status pairs (covers custom initial JSON blocks)
        if (eventStatusMap.Count == 0)
        {
            var rx = new Regex("\\\"id\\\"\\s*:\\s*\\\"(?<id>s-[^\\\"]+)\\\"[\\\\s\\\\S]*?\\\"status\\\"\\s*:\\s*\\\"(?<status>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            foreach (Match m in rx.Matches(html))
            {
                try
                {
                    var id = m.Groups["id"].Value;
                    var s = m.Groups["status"].Value;
                    if (!string.IsNullOrEmpty(id) && !eventStatusMap.ContainsKey(id))
                    {
                        eventStatusMap[id] = (string.Empty, s, string.Empty);
                    }
                }
                catch { }
            }
        }

        // --- NEW SERIAL SCANNER ---
        var swSerial = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ScanHtmlSerial(html, list, eventStatusMap);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BBC] Serial scan failed");
        }
        swSerial.Stop();

        sw.Stop();
        
        // Log "Serial" instead of BlockParse parts
        logger.LogInformation("[BBC] Parsing stats: HeaderRx=0ms MapStream={Map}ms GameRx=0ms SerialScan={Serial}ms Total={Total}ms", 
            swMap.ElapsedMilliseconds, swSerial.ElapsedMilliseconds, sw.ElapsedMilliseconds);

        return list;
    }

    private void ScanHtmlSerial(string html, List<BbcFixture> list, Dictionary<string, (string periodLabel, string status, string statusComment)> eventStatusMap)
    {
        // Single pass scanner
        // We look for "<h2" or "data-event-id=" markers serially
        int cursor = 0;
        string currentLeague = string.Empty;
        int len = html.Length;
        int gamesFound = 0;

        while (cursor < len)
        {
            // Find next interesting marker
            // We search for "<h2" and "data-event-id="
            int nextH2 = html.IndexOf("<h2", cursor, StringComparison.OrdinalIgnoreCase);
            int nextGame = html.IndexOf("data-event-id=", cursor, StringComparison.OrdinalIgnoreCase);

            if (nextH2 < 0 && nextGame < 0) break; // done

            // Determine which comes first
            bool isHeader = false;
            int foundIdx = -1;

            if (nextH2 >= 0 && nextGame >= 0)
            {
                if (nextH2 < nextGame) { isHeader = true; foundIdx = nextH2; }
                else { isHeader = false; foundIdx = nextGame; }
            }
            else if (nextH2 >= 0) { isHeader = true; foundIdx = nextH2; }
            else { isHeader = false; foundIdx = nextGame; }

            // Move cursor past the finding
            cursor = foundIdx;

            if (isHeader)
            {
                // Parse League Header
                // <h2 ...>Content</h2>
                int endH2 = html.IndexOf("</h2>", cursor, StringComparison.OrdinalIgnoreCase);
                if (endH2 > cursor)
                {
                    // Find closing '>' of open tag
                    int closeTag = html.IndexOf('>', cursor);
                    if (closeTag > cursor && closeTag < endH2)
                    {
                        var content = html.Substring(closeTag + 1, endH2 - closeTag - 1);
                        var text = StripTags(content);
                        text = System.Net.WebUtility.HtmlDecode(text);
                        if (!string.IsNullOrWhiteSpace(text) && !text.Contains("Scores & Fixtures", StringComparison.OrdinalIgnoreCase))
                        {
                            currentLeague = text;
                        }
                    }
                    cursor = endH2 + 5; // skip </h2>
                }
                else
                {
                    cursor += 4; // safety skip
                }
            }
            else
            {
                // Parse Game
                // data-event-id="s-..."
                // We need to define scope. Where does this game block end?
                // It ends at next "data-event-id=" OR next "<h2" OR a reasonable limit.
                // Actually, we can just extract fields *forward* from here until we hit something or are happy.
                // But better to define a 'limit' index to avoid scanning into next game.
                
                int nextMarker1 = html.IndexOf("data-event-id=", cursor + 14, StringComparison.OrdinalIgnoreCase);
                int nextMarker2 = html.IndexOf("<h2", cursor + 14, StringComparison.OrdinalIgnoreCase);
                int limit = len;
                if (nextMarker1 >= 0 && nextMarker1 < limit) limit = nextMarker1;
                if (nextMarker2 >= 0 && nextMarker2 < limit) limit = nextMarker2;

                // Extract ID
                string id = "";
                int quoteStart = html.IndexOf('"', cursor + 14); // after data-event-id=
                if (quoteStart >= 0 && quoteStart < limit)
                {
                    int quoteEnd = html.IndexOf('"', quoteStart + 1);
                    if (quoteEnd > quoteStart && quoteEnd < limit)
                    {
                        id = html.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                    }
                }

                // If valid ID, extract details in range [cursor, limit)
                if (!string.IsNullOrEmpty(id))
                {
                    var swGame = System.Diagnostics.Stopwatch.StartNew();
                    // Use helper that takes (html, start, end) to avoid substring
                    ParseGameNative(html, cursor, limit, id, currentLeague, list, eventStatusMap);
                    swGame.Stop();
                    if (swGame.ElapsedMilliseconds > Timeout)
                    {
                         logger.LogWarning("[BBC] Slow game parse ({Elapsed}ms) for ID {Id} in League {League}", swGame.ElapsedMilliseconds, id, currentLeague);
                    }
                    gamesFound++;
                }

                cursor = limit; // jump to next item
            }
        }
    }

    private static void ParseGameNative(string html, int start, int end, string id, string currentLeague, List<BbcFixture> list, Dictionary<string, (string periodLabel, string status, string statusComment)> eventStatusMap)
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
                    
                    // If not found in limited range, search further but cap at 15KB to prevent performance issues
                    // This handles edge cases while avoiding the 300ms+ penalty for games at end of large pages
                    if (cEnd < 0)
                    {
                        int maxSearch = Math.Min(html.Length - cStart, 15000);
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
            if (!string.IsNullOrEmpty(progressInner))
            {
                var injPlus = Regex.Match(progressInner, @"(?<m>\d+)\s*\+\s*(?<e>\d+)", RegexOptions.IgnoreCase);
                if (injPlus.Success)
                {
                    var m = TryParseInt(injPlus.Groups["m"].Value) ?? 0;
                    var e = TryParseInt(injPlus.Groups["e"].Value) ?? 0;
                    minute = m * 100 + e;
                    minuteStatus = $"{m}+{e}'";
                }
                else
                {
                    var mMatch = Regex.Match(progressInner, @"(?<m>\d+)\s*'", RegexOptions.IgnoreCase);
                    if (!mMatch.Success) mMatch = Regex.Match(progressInner, @"(?<m>\d+)\s*minutes", RegexOptions.IgnoreCase);
                    if (mMatch.Success) minute = TryParseInt(mMatch.Groups["m"].Value);
                    if (minute.HasValue) minuteStatus = $"{minute}'";
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
                    // Fallback to unbounded search if bounded search fails
                    return html.IndexOf(value, searchStart, StringComparison.OrdinalIgnoreCase) >= 0;
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
            var hasLive = hasProgressContainer && (
                progressInner.IndexOf("in progress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                progressInner.IndexOf("Live", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RangeContains("Live"));

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
                int searchBackLimit = Math.Max(start, firstBadgeIdx - Timeout);
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
                             int searchBackLimit2 = Math.Max(start, secondBadgeIdx - Timeout);
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
                     var blockSub = html.Substring(start, rangeLen);
                     var penMatch = Regex.Match(blockSub, @"(?<winner>[^<>\n]{1,100}?)\s+win\s+(?<w>\d+)\s*-\s*(?<l>\d+)\s+on\s+penalties", RegexOptions.IgnoreCase);
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
                 if (string.IsNullOrEmpty(status) || status.Equals("", StringComparison.OrdinalIgnoreCase))
                 {
                     status = statusMap.status;
                     if (status.IndexOf("Postponed", StringComparison.OrdinalIgnoreCase) >= 0)
                     {
                         isPostponed = true;
                         hasProgress = false;
                         isFinished = false;
                         isInProgress = false;
                         isHalf = false;
                         minute = null;
                     }
                     else if (Regex.IsMatch(status, @"\b\d+\s*(st|nd|rd|th)\b", RegexOptions.IgnoreCase))
                     {
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

            list.Add(new BbcFixture(home, away, DateTime.MinValue, status, isFinished, isInProgress, isHalf, minute, homeScore, awayScore, homeBadge, awayBadge, currentLeague, hasProgress, afterExtraTime, penaltyWinner, penaltyWinnerGoals, penaltyLoserGoals));
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
