using VardyParty.Kernel;

namespace VardyParty.Presentation;

/// <summary>One Netflix-style row: a league and its ordered matches.</summary>
public sealed record LeagueRowModel(string League, IReadOnlyList<Game> Games)
{
    public bool HasLiveGames => Games.Any(g => g.IsLiveForOrdering);
}

/// <summary>
/// Groups an already-filtered, already-ordered flat game list into league rows.
/// Rows with live matches come first, then rows by earliest kick-off.
/// Match order inside a row is preserved from the input (live → upcoming).
/// </summary>
public static class HomeRowsBuilder
{
    public const string FallbackLeague = "Other";

    public static IReadOnlyList<LeagueRowModel> Build(IEnumerable<Game>? games)
    {
        var list = games?.Where(g => g != null).ToList() ?? new List<Game>();
        if (list.Count == 0) return Array.Empty<LeagueRowModel>();

        return list
            .GroupBy(
                g => string.IsNullOrWhiteSpace(g.DisplayLeague) ? FallbackLeague : g.DisplayLeague,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Row = new LeagueRowModel(group.Key, group.ToList()),
                HasLive = group.Any(g => g.IsLiveForOrdering),
                EarliestStart = group.Min(g => g.StartUtcForOrdering),
            })
            .OrderByDescending(r => r.HasLive)
            .ThenBy(r => r.EarliestStart)
            .ThenBy(r => r.Row.League, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Row)
            .ToList();
    }
}
