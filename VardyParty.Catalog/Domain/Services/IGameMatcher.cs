using VardyParty.Models;

namespace VardyParty.Services;

public interface IGameMatcher
{
    void EnrichGames(List<Game> games, List<BbcFixture> bbcFixtures, string leagueLabel);
}
