using VardyParty.Kernel;

namespace VardyParty.Catalog;

/// <summary>
/// Homepage retention for a finished fixture. The catalog has no end time for
/// full time, extra time, or penalties, so the clock starts the first time
/// this process sees a scored finish. The card stays for
/// <see cref="Duration"/>, then drops. A later poll of the same fixture does
/// not restart the clock.
/// </summary>
public sealed class FinishedGameGrace
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Process-wide clock used by <see cref="DisplayExtensions.ToDisplay"/>.
    /// Survives catalog polls that replace <see cref="Game"/> instances.
    /// </summary>
    public static FinishedGameGrace Shared { get; } = new();

    private readonly Dictionary<string, DateTime> _firstSeen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Record a first observation for each scored finish in
    /// <paramref name="games"/>. Stamps for fixtures that have left the
    /// snapshot are dropped once they are older than <see cref="Duration"/>.
    /// </summary>
    public void Observe(IEnumerable<Game> games, DateTime utcNow)
    {
        lock (_gate)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var game in games)
            {
                if (!ScoresTickerPolicy.IsFinishedWithScore(game))
                {
                    continue;
                }

                var key = game.FixtureKey;
                present.Add(key);
                if (!_firstSeen.ContainsKey(key))
                {
                    _firstSeen[key] = utcNow;
                }
            }

            if (_firstSeen.Count == 0)
            {
                return;
            }

            List<string>? stale = null;
            foreach (var pair in _firstSeen)
            {
                if (!present.Contains(pair.Key) && utcNow - pair.Value >= Duration)
                {
                    stale ??= [];
                    stale.Add(pair.Key);
                }
            }

            if (stale is null)
            {
                return;
            }

            foreach (var key in stale)
            {
                _firstSeen.Remove(key);
            }
        }
    }

    public bool IsWithinGrace(Game game, DateTime utcNow)
    {
        if (!ScoresTickerPolicy.IsFinishedWithScore(game))
        {
            return false;
        }

        lock (_gate)
        {
            return _firstSeen.TryGetValue(game.FixtureKey, out var seen)
                && utcNow - seen < Duration;
        }
    }
}
