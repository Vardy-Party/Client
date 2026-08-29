using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>The notification-worthy things that can happen to a tracked game.</summary>
public enum MatchEventKind
{
    /// <summary>A live game's score increased.</summary>
    Goal,

    /// <summary>The game's phase moved INTO extra time.</summary>
    ExtraTime,

    /// <summary>The game's phase moved INTO a penalty shoot-out.</summary>
    Penalties,
}

/// <summary>Which side's score moved on a <see cref="MatchEventKind.Goal"/>.</summary>
public enum GoalSide
{
    Home,
    Away,

    /// <summary>Both scores moved in one poll gap (missed update).</summary>
    Both,
}

/// <summary>
/// One detected match event with its scoring context. <see cref="HomeScore"/>
/// and <see cref="AwayScore"/> are the NEW (post-event) scores.
/// </summary>
public sealed record MatchEvent(
    MatchEventKind Kind,
    Game Game,
    int HomeScore,
    int AwayScore,
    GoalSide? ScoringSide = null);

/// <summary>
/// Pure detector for match events, generalizing the old score-only
/// ScoreChangeDetector. Feed it every catalog update — the FILTERED display
/// list, so games in hidden leagues are never observed and never fire. It
/// reports, per update:
/// - GOAL: a live game's observed score moved up between two updates (score
///   corrections downwards are ignored, FT corrections are ignored);
/// - EXTRA TIME / PENALTIES: an observed game's phase transitioned INTO
///   <see cref="MatchPhase.ExtraTime"/> / <see cref="MatchPhase.Penalties"/>.
/// A game's first observation never counts (first load, a game appearing
/// mid-match, or a league being unhidden must not fire the sting).
/// </summary>
public sealed class MatchEventDetector
{
    private readonly Dictionary<string, (int Home, int Away, MatchPhase Phase)> _observed =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records the update and returns the events since the previous
    /// observation. Never raises on a game's first appearance.
    /// </summary>
    public IReadOnlyList<MatchEvent> Observe(IEnumerable<Game>? games)
    {
        if (games == null)
        {
            return Array.Empty<MatchEvent>();
        }

        var events = new List<MatchEvent>();
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
            var phase = MatchStatusPresenter.GetPhase(game);

            if (_observed.TryGetValue(key, out var previous))
            {
                var homeScored = home > previous.Home;
                var awayScored = away > previous.Away;
                if ((homeScored || awayScored) && game.IsLiveForOrdering)
                {
                    var side = homeScored && awayScored
                        ? GoalSide.Both
                        : homeScored ? GoalSide.Home : GoalSide.Away;
                    events.Add(new MatchEvent(MatchEventKind.Goal, game, home, away, side));
                }

                if (phase == MatchPhase.ExtraTime && previous.Phase != MatchPhase.ExtraTime)
                {
                    events.Add(new MatchEvent(MatchEventKind.ExtraTime, game, home, away));
                }
                else if (phase == MatchPhase.Penalties && previous.Phase != MatchPhase.Penalties)
                {
                    events.Add(new MatchEvent(MatchEventKind.Penalties, game, home, away));
                }
            }

            _observed[key] = (home, away, phase);
        }

        return events;
    }

    /// <summary>Forget everything (e.g. after sign-out) so re-appearing games don't fire.</summary>
    public void Reset() => _observed.Clear();

    private static string KeyOf(Game game) => $"{game.Home}|{game.Away}|{game.Start:O}";
}
