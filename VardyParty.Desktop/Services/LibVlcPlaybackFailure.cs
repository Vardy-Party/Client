namespace VardyParty.Desktop.Services;

/// <summary>
/// Classifies LibVLC log lines that mean the current URL cannot play.
/// Keep this conservative: WSL/LibVLC logs mix HTTP status, bitrate
/// numbers, and access-module names. A false 403 failover kills a
/// healthy stream on Linux.
/// </summary>
public static class LibVlcPlaybackFailure
{
    public static bool IsFatalAdaptiveDemux(string? module, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return (string.IsNullOrEmpty(module) ||
                module.Contains("adaptive", StringComparison.OrdinalIgnoreCase)) &&
               message.Contains("Failed to create demuxer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHttpForbidden(string? module, string? message)
    {
        _ = module;
        return ContainsHttpStatus(message, 403);
    }

    public static bool ContainsHttpStatus(string? message, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(message) || statusCode is < 100 or > 599)
            return false;

        var code = statusCode.ToString();
        var span = message.AsSpan();
        const string http = "HTTP";
        var start = 0;
        while (start < span.Length)
        {
            var httpAt = span[start..].IndexOf(http, StringComparison.OrdinalIgnoreCase);
            if (httpAt < 0)
                return false;

            var i = start + httpAt + http.Length;
            if (i < span.Length && span[i] == '/')
            {
                i++;
                while (i < span.Length && (char.IsDigit(span[i]) || span[i] == '.'))
                    i++;
            }

            while (i < span.Length && char.IsWhiteSpace(span[i]))
                i++;

            if (i + code.Length <= span.Length &&
                span.Slice(i, code.Length).Equals(code, StringComparison.Ordinal) &&
                (i + code.Length == span.Length || !char.IsDigit(span[i + code.Length])))
            {
                return true;
            }

            start += httpAt + 1;
        }

        return false;
    }
}
