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
    private const string EscapedStringEnd = "\\\"";

    private const string UnescapedIdMarker = "\"id\":\"s-";
    private const int EventFieldWindow = 3000;
    private const int MaxObjects = 5000;

    public BbcJsonParser(ILogger<BbcJsonParser>? logger = null)
    {
        _logger = logger ?? NullLogger<BbcJsonParser>.Instance;
    }

    public Dictionary<string, (string periodLabel, string status, string statusComment)> BuildEventStatusMapStreaming(string html, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, (string periodLabel, string status, string statusComment)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return map;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var searchStart = 0;
            var anchorIdx = html.IndexOf("__INITIAL_DATA__", StringComparison.OrdinalIgnoreCase);
            if (anchorIdx >= 0) searchStart = anchorIdx;

            // Real BBC pages use escaped JSON inside a quoted JS/HTML string.
            ExtractEscapedEventStatuses(html, searchStart, map, cancellationToken);

            // Unit tests / older markup may embed raw JSON objects.
            if (map.Count == 0)
            {
                ExtractUnescapedEventStatuses(html, searchStart, map, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BBC] BuildEventStatusMapStreaming failed");
        }

        return map;
    }

    private void ExtractEscapedEventStatuses(
        string html,
        int searchStart,
        Dictionary<string, (string periodLabel, string status, string statusComment)> map,
        CancellationToken cancellationToken)
    {
        var pos = searchStart;
        var found = 0;

        while (found < MaxObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var idMarkerPos = html.IndexOf(EscapedIdMarker, pos, StringComparison.Ordinal);
            if (idMarkerPos < 0) break;

            var idStart = idMarkerPos + EscapedIdMarker.Length;
            if (idStart + 2 >= html.Length || html[idStart] != 's' || html[idStart + 1] != '-')
            {
                pos = idMarkerPos + EscapedIdMarker.Length;
                continue;
            }

            var idEnd = html.IndexOf(EscapedStringEnd, idStart, StringComparison.Ordinal);
            if (idEnd <= idStart)
            {
                pos = idStart + 1;
                continue;
            }

            var id = html.Substring(idStart, idEnd - idStart);
            var windowEnd = Math.Min(html.Length, idMarkerPos + EventFieldWindow);
            var windowLen = windowEnd - idMarkerPos;

            var status = ExtractEscapedStringValue(html, EscapedStatusMarker, idMarkerPos, windowLen);
            var period = ExtractEscapedStringValue(html, EscapedPeriodValueMarker, idMarkerPos, windowLen);
            var statusComment = ExtractEscapedStringValue(html, EscapedStatusCommentValueMarker, idMarkerPos, windowLen);

            // Only keep event-like objects that expose a status field.
            if (!string.IsNullOrEmpty(status) && !map.ContainsKey(id))
            {
                map[id] = (period, status, statusComment);
                found++;
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

    private void ExtractUnescapedEventStatuses(
        string html,
        int searchStart,
        Dictionary<string, (string periodLabel, string status, string statusComment)> map,
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

                        if (!map.ContainsKey(id))
                        {
                            map[id] = (period, status, statusComment);
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
