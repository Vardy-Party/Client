using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace VardyParty.Parsers;

public class BbcJsonParser : IBbcJsonParser
{
    private readonly ILogger<BbcJsonParser> _logger;

    // BBC currently embeds __INITIAL_DATA__ as an escaped JSON string:
    //   __INITIAL_DATA__="{\"data\":...,\"id\":\"s-...\",\"status\":\"PostEvent\",...}"
    private const string EscapedIdMarker = "\\\"id\\\":\\\"";
    private const string EscapedStatusMarker = "\\\"status\\\":\\\"";
    private const string EscapedPeriodValueMarker = "\\\"periodLabel\\\":{\\\"value\\\":\\\"";
    private const string EscapedStatusCommentValueMarker = "\\\"statusComment\\\":{\\\"value\\\":\\\"";
    private const string EscapedStartDateTimeMarker = "\\\"startDateTime\\\":\\\"";
    private const string EscapedStringEnd = "\\\"";

    private const string UnescapedIdMarker = "\"id\":\"s-";
    private const int EventFieldWindow = 3000;
    private const int IdLookbackChars = 800;
    private const int MaxObjects = 5000;

    public BbcJsonParser(ILogger<BbcJsonParser>? logger = null)
    {
        _logger = logger ?? NullLogger<BbcJsonParser>.Instance;
    }

    public Dictionary<string, (string periodLabel, string status, string statusComment)> BuildEventStatusMapStreaming(
        string html,
        CancellationToken cancellationToken = default)
        => BuildEventMapsStreaming(html, cancellationToken).StatusByEventId;

    public (Dictionary<string, (string periodLabel, string status, string statusComment)> StatusByEventId,
        Dictionary<string, DateTime> KickoffUtcByEventId) BuildEventMapsStreaming(
        string html,
        CancellationToken cancellationToken = default)
    {
        // Typical heavy match day is ~300 fixtures; pre-size to cut dictionary growth churn.
        var statusMap = new Dictionary<string, (string periodLabel, string status, string statusComment)>(384, StringComparer.OrdinalIgnoreCase);
        var kickoffMap = new Dictionary<string, DateTime>(384, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return (statusMap, kickoffMap);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (searchStart, searchEnd) = GetInitialDataSearchBounds(html);

            // Real BBC pages use escaped JSON inside a quoted JS/HTML string.
            // Prefer startDateTime-driven scan (~1 hit/fixture). Fall back to id scan for
            // synthetic HTML that has status without kickoff fields.
            ExtractEscapedEventMaps(html, searchStart, searchEnd, statusMap, kickoffMap, cancellationToken);
            if (statusMap.Count == 0)
            {
                ExtractEscapedEventMapsById(html, searchStart, searchEnd, statusMap, kickoffMap, cancellationToken);
            }

            // Unit tests / older markup may embed raw JSON objects.
            if (statusMap.Count == 0)
            {
                ExtractUnescapedEventMaps(html, searchStart, statusMap, kickoffMap, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BBC] BuildEventMapsStreaming failed");
        }

        return (statusMap, kickoffMap);
    }

    private static (int Start, int End) GetInitialDataSearchBounds(string html)
    {
        var anchorIdx = html.IndexOf("__INITIAL_DATA__", StringComparison.OrdinalIgnoreCase);
        if (anchorIdx < 0) return (0, html.Length);

        var eq = html.IndexOf('=', anchorIdx);
        if (eq < 0) return (anchorIdx, html.Length);

        var quoteStart = html.IndexOf('"', eq + 1);
        if (quoteStart < 0) return (anchorIdx, html.Length);

        // Payload ends at the closing quote before "; (content uses \").
        var quoteEnd = html.IndexOf("\";", quoteStart + 1, StringComparison.Ordinal);
        if (quoteEnd < 0)
        {
            var scriptEnd = html.IndexOf("</script>", quoteStart + 1, StringComparison.OrdinalIgnoreCase);
            return (quoteStart + 1, scriptEnd > quoteStart ? scriptEnd : html.Length);
        }

        return (quoteStart + 1, quoteEnd);
    }

    private void ExtractEscapedEventMaps(
        string html,
        int searchStart,
        int searchEnd,
        Dictionary<string, (string periodLabel, string status, string statusComment)> statusMap,
        Dictionary<string, DateTime> kickoffMap,
        CancellationToken cancellationToken)
    {
        if (searchEnd <= searchStart) return;

        var pos = searchStart;
        var found = 0;
        // Drive from startDateTime (~1 per fixture) instead of every \"id\":\" (~7× more on real pages).
        var available = searchEnd - searchStart;

        while (found < MaxObjects && available > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dtMarkerPos = html.IndexOf(EscapedStartDateTimeMarker, pos, available, StringComparison.Ordinal);
            if (dtMarkerPos < 0 || dtMarkerPos >= searchEnd) break;

            var dtStart = dtMarkerPos + EscapedStartDateTimeMarker.Length;
            var dtRemaining = searchEnd - dtStart;
            if (dtRemaining <= 0) break;

            var dtEnd = html.IndexOf(EscapedStringEnd, dtStart, Math.Min(48, dtRemaining), StringComparison.Ordinal);
            if (dtEnd <= dtStart)
            {
                pos = dtMarkerPos + EscapedStartDateTimeMarker.Length;
                available = searchEnd - pos;
                continue;
            }

            var lookback = Math.Min(IdLookbackChars, dtMarkerPos - searchStart);
            var idMarkerPos = lookback > 0
                ? html.LastIndexOf(EscapedIdMarker, dtMarkerPos, lookback, StringComparison.Ordinal)
                : -1;

            if (idMarkerPos < searchStart)
            {
                pos = dtEnd + EscapedStringEnd.Length;
                available = searchEnd - pos;
                continue;
            }

            var idStart = idMarkerPos + EscapedIdMarker.Length;
            if (idStart + 2 >= searchEnd || html[idStart] != 's' || html[idStart + 1] != '-')
            {
                pos = dtEnd + EscapedStringEnd.Length;
                available = searchEnd - pos;
                continue;
            }

            var idEnd = html.IndexOf(EscapedStringEnd, idStart, Math.Min(80, searchEnd - idStart), StringComparison.Ordinal);
            if (idEnd <= idStart)
            {
                pos = dtEnd + EscapedStringEnd.Length;
                available = searchEnd - pos;
                continue;
            }

            var id = html.Substring(idStart, idEnd - idStart);
            var windowLen = Math.Min(EventFieldWindow, searchEnd - idMarkerPos);
            var status = ExtractEscapedStringValue(html, EscapedStatusMarker, idMarkerPos, windowLen);
            var period = ExtractEscapedStringValue(html, EscapedPeriodValueMarker, idMarkerPos, windowLen);
            var statusComment = ExtractEscapedStringValue(html, EscapedStatusCommentValueMarker, idMarkerPos, windowLen);
            var startDateTime = html.Substring(dtStart, dtEnd - dtStart);

            if (!string.IsNullOrEmpty(status) && !statusMap.ContainsKey(id))
            {
                statusMap[id] = (period, status, statusComment);
                found++;
            }

            if (!kickoffMap.ContainsKey(id) && TryParseKickoffUtc(startDateTime, out var kickoffUtc))
            {
                kickoffMap[id] = kickoffUtc;
            }

            pos = dtEnd + EscapedStringEnd.Length;
            available = searchEnd - pos;
        }
    }

    private void ExtractEscapedEventMapsById(
        string html,
        int searchStart,
        int searchEnd,
        Dictionary<string, (string periodLabel, string status, string statusComment)> statusMap,
        Dictionary<string, DateTime> kickoffMap,
        CancellationToken cancellationToken)
    {
        if (searchEnd <= searchStart) return;

        var pos = searchStart;
        var found = 0;

        while (found < MaxObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = searchEnd - pos;
            if (remaining <= 0) break;

            var idMarkerPos = html.IndexOf(EscapedIdMarker, pos, remaining, StringComparison.Ordinal);
            if (idMarkerPos < 0 || idMarkerPos >= searchEnd) break;

            var idStart = idMarkerPos + EscapedIdMarker.Length;
            if (idStart + 2 >= searchEnd || html[idStart] != 's' || html[idStart + 1] != '-')
            {
                pos = idMarkerPos + EscapedIdMarker.Length;
                continue;
            }

            var idEnd = html.IndexOf(EscapedStringEnd, idStart, Math.Min(80, searchEnd - idStart), StringComparison.Ordinal);
            if (idEnd <= idStart)
            {
                pos = idStart + 1;
                continue;
            }

            var id = html.Substring(idStart, idEnd - idStart);
            var windowLen = Math.Min(EventFieldWindow, searchEnd - idMarkerPos);

            var status = ExtractEscapedStringValue(html, EscapedStatusMarker, idMarkerPos, windowLen);
            var period = ExtractEscapedStringValue(html, EscapedPeriodValueMarker, idMarkerPos, windowLen);
            var statusComment = ExtractEscapedStringValue(html, EscapedStatusCommentValueMarker, idMarkerPos, windowLen);
            var startDateTime = ExtractEscapedStringValue(html, EscapedStartDateTimeMarker, idMarkerPos, windowLen);

            if (!string.IsNullOrEmpty(status) && !statusMap.ContainsKey(id))
            {
                statusMap[id] = (period, status, statusComment);
                found++;
            }

            if (!string.IsNullOrEmpty(startDateTime)
                && !kickoffMap.ContainsKey(id)
                && TryParseKickoffUtc(startDateTime, out var kickoffUtc))
            {
                kickoffMap[id] = kickoffUtc;
            }

            pos = idEnd + EscapedStringEnd.Length;
        }
    }

    private static string ExtractEscapedStringValue(string html, string marker, int searchStart, int searchLength)
    {
        if (searchLength <= 0) return string.Empty;

        var markerPos = html.IndexOf(marker, searchStart, searchLength, StringComparison.Ordinal);
        if (markerPos < 0) return string.Empty;

        var valueStart = markerPos + marker.Length;
        var remaining = searchStart + searchLength - valueStart;
        if (remaining <= 0) return string.Empty;

        var valueEnd = html.IndexOf(EscapedStringEnd, valueStart, remaining, StringComparison.Ordinal);
        if (valueEnd <= valueStart) return string.Empty;

        return html.Substring(valueStart, valueEnd - valueStart);
    }

    private void ExtractUnescapedEventMaps(
        string html,
        int searchStart,
        Dictionary<string, (string periodLabel, string status, string statusComment)> statusMap,
        Dictionary<string, DateTime> kickoffMap,
        CancellationToken cancellationToken)
    {
        var searchPos = searchStart;
        var found = 0;

        while (found < MaxObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idPos = html.IndexOf(UnescapedIdMarker, searchPos, StringComparison.Ordinal);
            if (idPos < 0) break;

            var objStart = html.LastIndexOf('{', idPos);
            if (objStart < searchStart)
            {
                searchPos = idPos + UnescapedIdMarker.Length;
                continue;
            }

            var objEnd = FindJsonObjectEnd(html, objStart);
            if (objEnd <= objStart)
            {
                searchPos = idPos + UnescapedIdMarker.Length;
                continue;
            }

            var objJson = html.Substring(objStart, objEnd - objStart + 1);
            try
            {
                using var doc = JsonDocument.Parse(objJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == JsonValueKind.String)
                {
                    var id = idProp.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(id) && id.StartsWith("s-", StringComparison.Ordinal))
                    {
                        var period = string.Empty;
                        var status = string.Empty;
                        var statusComment = string.Empty;
                        if (root.TryGetProperty("periodLabel", out var pl)
                            && pl.ValueKind == JsonValueKind.Object
                            && pl.TryGetProperty("value", out var pv))
                        {
                            period = pv.GetString() ?? string.Empty;
                        }

                        if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                        {
                            status = st.GetString() ?? string.Empty;
                        }

                        if (root.TryGetProperty("statusComment", out var sc)
                            && sc.ValueKind == JsonValueKind.Object
                            && sc.TryGetProperty("value", out var scv))
                        {
                            statusComment = scv.GetString() ?? string.Empty;
                        }

                        if (!statusMap.ContainsKey(id))
                        {
                            statusMap[id] = (period, status, statusComment);
                        }

                        if (!kickoffMap.ContainsKey(id)
                            && root.TryGetProperty("startDateTime", out var sdt)
                            && sdt.ValueKind == JsonValueKind.String
                            && TryParseKickoffUtc(sdt.GetString(), out var kickoffUtc))
                        {
                            kickoffMap[id] = kickoffUtc;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BBC] Failed to parse event JSON object");
            }

            found++;
            searchPos = objEnd + 1;
        }
    }

    internal static bool TryParseKickoffUtc(string? value, out DateTime kickoffUtc)
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

    private static int FindJsonObjectEnd(string html, int objStart)
    {
        var depth = 0;
        var inString = false;
        for (var i = objStart; i < html.Length; i++)
        {
            var c = html[i];
            if (c == '"')
            {
                var back = i - 1;
                var esc = false;
                while (back >= 0 && html[back] == '\\')
                {
                    esc = !esc;
                    back--;
                }

                if (!esc) inString = !inString;
            }

            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }
}
