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
/// Row titles use <see cref="LeagueDisplayName"/> so a case-insensitive group
/// (API ALL CAPS plus a BBC title-case spelling) shows one preferred heading
/// without changing <see cref="Game.ApiLeague"/>.
/// </summary>
public static class HomeRowsBuilder
{
    public const string FallbackLeague = "Other";

    public static IReadOnlyList<LeagueRowModel> Build(IEnumerable<Game>? games)
    {
        var list = games?.Where(g => g != null).ToList() ?? new List<Game>();
        if (list.Count == 0) return Array.Empty<LeagueRowModel>();

        return list
            .GroupBy(GroupingKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Row = new LeagueRowModel(LeagueDisplayName.ForRow(group), group.ToList()),
                HasLive = group.Any(g => g.IsLiveForOrdering),
                EarliestStart = group.Min(g => g.StartUtcForOrdering),
            })
            .OrderByDescending(r => r.HasLive)
            .ThenBy(r => r.EarliestStart)
            .ThenBy(r => r.Row.League, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Row)
            .ToList();
    }

    private static string GroupingKey(Game game) =>
        string.IsNullOrWhiteSpace(game.DisplayLeague) ? FallbackLeague : game.DisplayLeague;
}
