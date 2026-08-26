using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>
/// Pure detector for genuine score changes. Feed it every catalog update;
/// it reports the games whose observed score moved between two updates while
/// live. A game's first observation never counts (first load / a game
/// appearing mid-match must not fire the goal sting), and score corrections
/// downwards are ignored.
/// </summary>
public sealed class ScoreChangeDetector
{
    private readonly Dictionary<string, (int Home, int Away)> _observed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records the update and returns the games with a new goal since the
    /// previous observation. Never raises on a game's first appearance.
    /// </summary>
    public IReadOnlyList<Game> Observe(IEnumerable<Game>? games)
    {
        if (games == null)
        {
            return Array.Empty<Game>();
        }

        var scorers = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            var key = KeyOf(game);
            if (!seen.Add(key))
            {
                continue; // duplicate entry in one update
            }

            var home = game.HomeScore ?? 0;
            var away = game.AwayScore ?? 0;

            if (_observed.TryGetValue(key, out var previous))
            {
                var increased = home > previous.Home || away > previous.Away;
                if (increased && game.IsLiveForOrdering)
                {
                    scorers.Add(game);
                }
            }

            _observed[key] = (home, away);
        }

        return scorers;
    }

    /// <summary>Forget everything (e.g. after sign-out) so re-appearing games don't fire.</summary>
    public void Reset() => _observed.Clear();

    private static string KeyOf(Game game) => $"{game.Home}|{game.Away}|{game.Start:O}";
}
