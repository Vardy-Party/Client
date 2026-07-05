namespace VardyParty.Services;

public static class LeagueFilterDefaults
{
    public static readonly HashSet<string> HiddenLeagues = new(StringComparer.OrdinalIgnoreCase)
    {
        "WWE", "Rugby", "NFL", "NHL", "NBA", "UFC", "Boxing", "Formula 1", "MotoGP", "Tennis", "Cricket", "Golf"
    };
}
