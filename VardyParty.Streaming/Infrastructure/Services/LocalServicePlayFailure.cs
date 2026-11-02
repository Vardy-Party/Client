using System.Text.Json;
using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// Reads a failed LocalService <c>GET /play</c> body and decides whether the
/// failure was "the CDN could not be reached" (defer, recheck later) rather
/// than "this page has no stream" (dead).
/// <para>
/// CDN unreachable when any of these hold:
/// <list type="bullet">
/// <item>HTTP 504: the page navigation timed out.</item>
/// <item>The <c>error</c> text carries a Chromium connect, reset, timeout or TLS
/// error (<c>net::ERR_CONNECTION_TIMED_OUT</c>, <c>net::ERR_CONNECTION_REFUSED</c>,
/// <c>net::ERR_SSL_*</c>, ...).</item>
/// <item><c>diagnostics.failedRequestHosts</c> has a media-like host (playlist or
/// segment requests) whose errors are of that class, or whose requests were
/// still pending (a TCP connect hanging) with no DNS failure.</item>
/// </list>
/// <c>net::ERR_NAME_NOT_RESOLVED</c> alone is never CDN unreachable: it stays
/// dead, as does a plain "M3U8 not Found" with no such evidence.
/// </para>
/// </summary>
public static class LocalServicePlayFailure
{
    public static bool IsCdnUnreachable(int statusCode, string? body) =>
        TryDescribeCdnUnreachable(statusCode, body, out _);

    /// <summary>As <see cref="IsCdnUnreachable"/>, with a short reason for logs and the deferred row.</summary>
    public static bool TryDescribeCdnUnreachable(int statusCode, string? body, out string reason)
    {
        reason = string.Empty;
        if (statusCode == 504)
        {
            reason = "page navigation timed out (504)";
            return true;
        }

        if (string.IsNullOrWhiteSpace(body))
            return false;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            if (CdnReachabilityPolicy.IsChromiumCdnUnreachable(body))
            {
                reason = "network error in response";
                return true;
            }

            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            var error = GetString(root, "error");
            if (CdnReachabilityPolicy.IsChromiumCdnUnreachable(error))
            {
                reason = Truncate(error!);
                return true;
            }

            if (TryGetProperty(root, "diagnostics", out var diagnostics)
                && diagnostics.ValueKind == JsonValueKind.Object
                && TryGetProperty(diagnostics, "failedRequestHosts", out var hosts)
                && hosts.ValueKind == JsonValueKind.Array)
            {
                foreach (var host in hosts.EnumerateArray())
                {
                    if (host.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!TryGetProperty(host, "mediaLike", out var mediaLike) || mediaLike.ValueKind != JsonValueKind.True)
                        continue;

                    var errors = new List<string>();
                    if (TryGetProperty(host, "errors", out var errorList) && errorList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in errorList.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                                errors.Add(text);
                        }
                    }

                    var hostName = GetString(host, "host") ?? "(unknown host)";
                    var connectError = errors.FirstOrDefault(CdnReachabilityPolicy.IsChromiumCdnUnreachable);
                    if (connectError != null)
                    {
                        reason = $"{hostName}: {connectError}";
                        return true;
                    }

                    var pending = TryGetProperty(host, "pending", out var pendingValue)
                                  && pendingValue.ValueKind == JsonValueKind.Number
                                  && pendingValue.TryGetInt32(out var count)
                        ? count
                        : 0;
                    var dnsFailure = errors.Any(e => e.Contains("ERR_NAME_NOT_RESOLVED", StringComparison.OrdinalIgnoreCase));
                    if (pending > 0 && !dnsFailure)
                    {
                        reason = $"{hostName}: {pending} media request(s) never answered";
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200];

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
