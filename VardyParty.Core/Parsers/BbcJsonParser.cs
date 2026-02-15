using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace VardyParty.Parsers;

public class BbcJsonParser : IBbcJsonParser
{
    private readonly ILogger<BbcJsonParser> _logger;

    public BbcJsonParser(ILogger<BbcJsonParser>? logger = null)
    {
        _logger = logger ?? NullLogger<BbcJsonParser>.Instance;
    }

    public Dictionary<string, (string periodLabel, string status, string statusComment)> BuildEventStatusMapStreaming(string html, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, (string periodLabel, string status, string statusComment)>();
        if (string.IsNullOrEmpty(html)) return map;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var anchorNames = new[] { "window.__INITIAL_DATA__", "__INITIAL_DATA__" };
            int anchorIdx = -1;
            foreach (var a in anchorNames)
            {
                anchorIdx = html.IndexOf(a, StringComparison.OrdinalIgnoreCase);
                if (anchorIdx >= 0) break;
            }
            if (anchorIdx < 0) return map;

            int braceStart = html.IndexOf('{', anchorIdx);
            if (braceStart < 0) return map;

            int searchPos = braceStart;
            int found = 0;
            const int MaxObjects = 5000; // safety

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int idPos = html.IndexOf("\"id\":\"s-", searchPos, StringComparison.OrdinalIgnoreCase);
                if (idPos < 0) break;

                int objStart = html.LastIndexOf('{', idPos);

                if (objStart < braceStart) break;

                int depth = 0;
                bool inString = false;
                int objEnd = -1;
                for (int i = objStart; i < html.Length; i++)
                {
                    char c = html[i];
                    if (c == '\"')
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
                                objEnd = i;
                                break;
                            }
                        }
                    }
                }

                if (objEnd <= objStart || objEnd < 0) break;

                var objJson = html.Substring(objStart, objEnd - objStart + 1);
                try
                {
                    using var doc = JsonDocument.Parse(objJson);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                        {
                            var id = idProp.GetString() ?? string.Empty;
                            if (!string.IsNullOrEmpty(id) && id.StartsWith("s-"))
                            {
                                string period = string.Empty, status = string.Empty, statusComment = string.Empty;
                                if (root.TryGetProperty("periodLabel", out var pl) && pl.ValueKind == JsonValueKind.Object && pl.TryGetProperty("value", out var pv)) period = pv.GetString() ?? string.Empty;
                                if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String) status = st.GetString() ?? string.Empty;
                                if (root.TryGetProperty("statusComment", out var sc) && sc.ValueKind == JsonValueKind.Object && sc.TryGetProperty("value", out var scv)) statusComment = scv.GetString() ?? string.Empty;
                                if (!map.ContainsKey(id)) map[id] = (period, status, statusComment);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BBC] Failed to parse event JSON object");
                }

                found++;
                if (found >= MaxObjects) break;

                searchPos = objEnd + 1;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BBC] BuildEventStatusMapStreaming failed");
        }

        return map;
    }
}
