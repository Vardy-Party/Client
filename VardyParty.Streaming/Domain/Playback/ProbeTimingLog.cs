using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// One greppable log line per probe-timing event, so real sessions can be collected and the
/// scheduling delays tuned from data (docs/PROBE_SCHEDULING_RULES.md, "Probe timing log").
/// Every line is <c>[probe-timing] event=NAME ms=N key=value ...</c> at Information level:
/// <c>ms</c> counts from the start of the session (<see cref="BeginSession"/>), values with
/// spaces are quoted, and URLs are reduced to their host (never a path or a token).
/// Thread-safe. One instance per <see cref="ResolverRaceThrottle"/> (one per app).
/// </summary>
public sealed class ProbeTimingLog
{
    /// <summary>The prefix of every line.</summary>
    public const string Prefix = "[probe-timing]";

    /// <summary>For callers with no throttle (tests, tools): counts from its first use.</summary>
    public static readonly ProbeTimingLog Detached = new();

    private readonly object _gate = new();
    private TimeProvider _time = TimeProvider.System;
    private DateTimeOffset? _start;

    /// <summary>Starts a session: <c>ms</c> counts from now on <paramref name="time"/>.</summary>
    public void BeginSession(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        lock (_gate)
        {
            _time = time;
            _start = time.GetUtcNow();
        }
    }

    /// <summary>Milliseconds since the session started.</summary>
    public long Ms
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                _start ??= now;
                return (long)Math.Max(0, (now - _start.Value).TotalMilliseconds);
            }
        }
    }

    /// <summary>Writes one line at Information level. Null fields are left out.</summary>
    public void Log(ILogger logger, string evt, params (string Key, object? Value)[] fields)
    {
        if (!logger.IsEnabled(LogLevel.Information))
            return;
        logger.LogInformation(Prefix + " {ProbeTiming}", Format(evt, fields));
    }

    /// <summary>The line after the prefix: <c>event=NAME ms=N key=value ...</c>.</summary>
    public string Format(string evt, params (string Key, object? Value)[] fields)
    {
        var sb = new StringBuilder();
        sb.Append("event=").Append(evt).Append(" ms=").Append(Ms.ToString(CultureInfo.InvariantCulture));
        foreach (var (key, value) in fields)
        {
            if (value is null)
                continue;
            sb.Append(' ').Append(key).Append('=').Append(Value(value));
        }

        return sb.ToString();
    }

    /// <summary>The host of <paramref name="url"/>, or null. Never the path or query (tokens).</summary>
    public static string? Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : null;

    /// <summary>The tier name used in every line: high, medium, finding, or none (no recommendations).</summary>
    public static string Tier(StreamTiers? tiers, VardyParty.Kernel.Stream? row) =>
        tiers is null ? "none" : tiers.TierOf(row) switch
        {
            StreamTier.High => "high",
            StreamTier.Medium => "medium",
            _ => "finding"
        };

    private static string Value(object value)
    {
        var text = value switch
        {
            bool b => b ? "true" : "false",
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        text = text.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
        return text.Length == 0 || text.Any(char.IsWhiteSpace) || text.Contains('=') ? "\"" + text + "\"" : text;
    }
}
