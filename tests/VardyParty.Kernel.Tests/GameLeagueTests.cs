using System.Text.Json;
using AutoFixture;
using VardyParty.Kernel;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class GameLeagueTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    private Game BuildGame(string league, string bbcLeague) =>
        _fixture.Build<Game>()
            .With(g => g.League, league)
            .With(g => g.BBCLeague, bbcLeague)
            .Create();

    [Fact]
    public void DisplayLeague_PrefersTheResolvedLeague_ThenBbcName_ThenLeague()
    {
        var game = BuildGame(league: " API League ", bbcLeague: " BBC League ");
        Assert.Equal("BBC League", game.DisplayLeague);

        game.ResolveLeague(" Resolved League ");
        Assert.Equal("Resolved League", game.DisplayLeague);
        Assert.Equal("BBC League", game.BBCLeague.Trim()); // resolving never touches BBCLeague

        var unmatched = BuildGame(league: " API League ", bbcLeague: string.Empty);
        Assert.Equal("API League", unmatched.DisplayLeague);
    }

    [Fact]
    public void ResolveLeague_TrimsAndToleratesNull()
    {
        var game = BuildGame("L", "B");
        game.ResolveLeague("  X  ");
        Assert.Equal("X", game.ResolvedLeague);

        game.ResolveLeague(null);
        Assert.Equal(string.Empty, game.ResolvedLeague);
        Assert.Equal("B", game.DisplayLeague);
    }

    [Fact]
    public void ResolvedLeague_IsNotPopulatedFromTheApiPayload()
    {
        var game = JsonSerializer.Deserialize<Game>(
            "{\"League\":\"L\",\"ResolvedLeague\":\"From payload\",\"BBCLeague\":\"B\"}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(string.Empty, game.ResolvedLeague);
        Assert.Equal("B", game.BBCLeague);
        Assert.Equal("B", game.DisplayLeague);
    }
}