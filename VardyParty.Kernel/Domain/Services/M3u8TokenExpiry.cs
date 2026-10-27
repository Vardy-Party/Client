using System.Text;
using System.Text.Json;

namespace VardyParty.Kernel;

/// <summary>
/// Reads the expiry baked into a signed m3u8 URL so playback can tell a token with
/// comfortable life left from one that must be re-resolved via <c>/play</c>.
/// Built-in shapes, first match wins:
/// <list type="bullet">
/// <item>Registered hooks (<see cref="Register"/>).</item>
/// <item>cowedd: <c>?t=&lt;unix&gt;.&lt;hex sig&gt;</c>.</item>
/// <item>Generic query keys <c>e</c>, <c>exp</c>, <c>expires</c>, <c>expiry</c>, <c>expire</c>, <c>x-expires</c>
/// (unix seconds or milliseconds).</item>
/// <item>JWT-shaped query values with an <c>exp</c> claim (sporty-aoura <c>token=</c>).</item>
/// <item>uzhi: a 10-digit unix path segment, e.g. <c>/main/secure/&lt;hex&gt;/&lt;unix&gt;/file.m3u8</c>.</item>
/// </list>
/// Values outside a plausible unix window never count, so ids and bitrates are not read as expiry.
/// </summary>
public static class M3u8TokenExpiry
{
    internal const long MinPlausibleUnix = 1_600_000_000;
    internal const long MaxPlausibleUnix = 4_102_444_800;

    private static readonly string[] GenericExpiryKeys =
        ["e", "exp", "expires", "expiry", "expire", "x-expires"];

    private static readonly object HookGate = new();
    private static Func<Uri, DateTimeOffset?>[] _hooks = [];

    /// <summary>
    /// Adds a source-specific expiry parser. Hooks run before the built-in shapes;
    /// return null when the URL is not yours. Exceptions are swallowed.
    /// </summary>
    public static void Register(Func<Uri, DateTimeOffset?> parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        lock (HookGate)
        {
            _hooks = [.. _hooks, parser];
        }
    }

    internal static void ResetHooksForTests()
    {
        lock (HookGate)
        {
            _hooks = [];
        }
    }

    public static bool TryGetExpiry(string? url, out DateTimeOffset expiry)
    {
        expiry = default;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var hook in Volatile.Read(ref _hooks))
        {
            try
            {
                if (hook(uri) is { } hit)
                {
                    expiry = hit;
                    return true;
                }
            }
            catch
            {
                // A broken hook must never block playback.
            }
        }

        var query = ParseQuery(uri.Query);
        return TryCowedd(query, out expiry)
            || TryGenericQuery(query, out expiry)
            || TryJwtQuery(query, out expiry)
            || TryPathSegment(uri, out expiry);
    }

    /// <summary>Time left on the token, or null when no expiry is readable.</summary>
    public static TimeSpan? RemainingLife(string? url, DateTimeOffset now)
        => TryGetExpiry(url, out var expiry) ? expiry - now : null;

    private static bool TryCowedd(List<KeyValuePair<string, string>> query, out DateTimeOffset expiry)
    {
        expiry = default;
        foreach (var (key, value) in query)
        {
            if (!key.Equals("t", StringComparison.OrdinalIgnoreCase))
                continue;
            var dot = value.IndexOf('.');
            if (dot <= 0 || dot == value.Length - 1)
                continue;
            if (!IsHex(value.AsSpan(dot + 1)))
                continue;
            if (TryUnix(value[..dot], out expiry))
                return true;
        }

        return false;
    }

    private static bool TryGenericQuery(List<KeyValuePair<string, string>> query, out DateTimeOffset expiry)
    {
        expiry = default;
        foreach (var (key, value) in query)
        {
            if (!GenericExpiryKeys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (TryUnix(value, out expiry))
                return true;
        }

        return false;
    }

    private static bool TryJwtQuery(List<KeyValuePair<string, string>> query, out DateTimeOffset expiry)
    {
        expiry = default;
        foreach (var (_, value) in query)
        {
            var parts = value.Split('.');
            if (parts.Length != 3 || parts[1].Length < 8)
                continue;
            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("exp", out var exp)
                    && exp.ValueKind == JsonValueKind.Number
                    && exp.TryGetInt64(out var seconds)
                    && TryUnix(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), out expiry))
                {
                    return true;
                }
            }
            catch
            {
                // Not a JWT.
            }
        }

        return false;
    }

    private static bool TryPathSegment(Uri uri, out DateTimeOffset expiry)
    {
        expiry = default;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Last plausible 10-digit segment that is not the file name itself.
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            var segment = segments[i];
            if (segment.Length == 10 && segment.All(char.IsAsciiDigit) && TryUnix(segment, out expiry))
                return true;
        }

        return false;
    }

    private static bool TryUnix(string raw, out DateTimeOffset expiry)
    {
        expiry = default;
        if (string.IsNullOrEmpty(raw) || !raw.All(char.IsAsciiDigit) || raw.Length > 13)
            return false;
        if (!long.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (raw.Length == 13)
            value /= 1000;
        if (value is < MinPlausibleUnix or > MaxPlausibleUnix)
            return false;
        expiry = DateTimeOffset.FromUnixTimeSeconds(value);
        return true;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        if (value.Length < 8)
            return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(query))
            return pairs;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            pairs.Add(new KeyValuePair<string, string>(
                Uri.UnescapeDataString(part[..eq]),
                Uri.UnescapeDataString(part[(eq + 1)..])));
        }

        return pairs;
    }
}
