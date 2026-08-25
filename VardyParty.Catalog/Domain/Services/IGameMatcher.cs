using VardyParty.Models;

namespace VardyParty.Catalog;

public interface IGameMatcher
{
    void EnrichGames(List<Game> games, List<BbcFixture> bbcFixtures, string leagueLabel);
}
