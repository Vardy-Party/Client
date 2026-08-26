using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using VardyParty.Kernel;
using Xunit;
using VardyParty.Catalog;
using VardyParty.TestSupport;

namespace VardyParty.Catalog.Tests;

public class LeagueFilterServiceTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    private static LeagueFilterService CreateService(InMemoryLeagueFilterPreferencesStore? store = null)
    {
        return new LeagueFilterService(store ?? new InMemoryLeagueFilterPreferencesStore());
    }

    [Fact]
    public void DefaultHiddenLeagues_AreApplied_WhenNoSavedPreferences()
    {
        // Arrange
        var svc = CreateService();
        var hiddenLeague = LeagueFilterDefaults.HiddenLeagues.First();
        const string visibleLeague = "League Alpha";

        // Act
        var hiddenVisible = svc.IsLeagueVisible(hiddenLeague);
        var alphaVisible = svc.IsLeagueVisible(visibleLeague);

        // Assert
        Assert.False(hiddenVisible);
        Assert.True(alphaVisible);
    }

    [Fact]
    public void FilterGames_ExcludesHiddenLeagues()
    {
        // Arrange
        var svc = CreateService();
        var hiddenLeague = LeagueFilterDefaults.HiddenLeagues.First();
        const string visibleLeague = "League Alpha";
        var games = new List<Game>
        {
            _fixture.Build<Game>()
                .With(g => g.League, visibleLeague)
                .With(g => g.BBCLeague, "")
                .With(g => g.Home, "Home United")
                .With(g => g.Away, "Away City")
                .Create(),
            _fixture.Build<Game>()
                .With(g => g.League, hiddenLeague)
                .With(g => g.BBCLeague, "")
                .With(g => g.Home, "North FC")
                .With(g => g.Away, "South FC")
                .Create()
        };

        // Act
        var filtered = svc.FilterGames(games);

        // Assert
        Assert.Single(filtered);
        Assert.Equal(visibleLeague, filtered[0].League);
    }

    [Fact]
    public void SetLeagueVisible_PersistsAndRaisesChanged()
    {
        // Arrange
        var store = new InMemoryLeagueFilterPreferencesStore();
        var svc = CreateService(store);
        var hiddenLeague = LeagueFilterDefaults.HiddenLeagues.First();
        var changed = false;
        svc.Changed += () => changed = true;

        // Act
        svc.SetLeagueVisible(hiddenLeague, true);

        // Assert
        Assert.True(svc.IsLeagueVisible(hiddenLeague));
        Assert.True(store.HasSavedPreferences);
        Assert.True(changed);
    }

    [Fact]
    public void SetLeaguesVisible_UpdatesMultipleLeaguesOnce()
    {
        // Arrange
        var store = new InMemoryLeagueFilterPreferencesStore();
        var svc = CreateService(store);
        var hiddenLeagues = LeagueFilterDefaults.HiddenLeagues.Take(2).ToArray();
        var changeCount = 0;
        svc.Changed += () => changeCount++;

        // Act
        svc.SetLeaguesVisible(hiddenLeagues, true);

        // Assert
        Assert.True(svc.IsLeagueVisible(hiddenLeagues[0]));
        Assert.True(svc.IsLeagueVisible(hiddenLeagues[1]));
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void ResetToDefaults_RestoresDefaultHiddenSet()
    {
        // Arrange
        var store = new InMemoryLeagueFilterPreferencesStore();
        var svc = CreateService(store);
        var hiddenLeague = LeagueFilterDefaults.HiddenLeagues.First();
        svc.SetLeagueVisible(hiddenLeague, true);

        // Act
        svc.ResetToDefaults();

        // Assert
        Assert.False(svc.IsLeagueVisible(hiddenLeague));
        Assert.False(store.HasSavedPreferences);
    }

    [Fact]
    public void GetKnownLeagues_ReturnsSortedLeagueNames()
    {
        // Arrange
        var svc = CreateService();
        var dict = new Dictionary<string, List<Game>>
        {
            ["League Gamma"] = [_fixture.Build<Game>().With(g => g.League, "League Gamma").With(g => g.BBCLeague, "").Create()],
            ["League Alpha"] = [_fixture.Build<Game>().With(g => g.League, "League Alpha").With(g => g.BBCLeague, "").Create()],
            ["League Beta"] = [_fixture.Build<Game>().With(g => g.League, "League Beta").With(g => g.BBCLeague, "").Create()]
        };

        // Act
        var leagues = svc.GetKnownLeagues(dict);

        // Assert
        Assert.Equal(new[] { "League Alpha", "League Beta", "League Gamma" }, leagues);
    }

    [Fact]
    public void GetKnownLeagues_UsesDisplayLeague_AndOmitsImportantGamesBucket()
    {
        var svc = CreateService();
        var ucl = _fixture.Build<Game>()
            .With(g => g.League, "Important Games")
            .With(g => g.BBCLeague, "UEFA Champions League")
            .Create();
        var leftover = _fixture.Build<Game>()
            .With(g => g.League, "Important Games")
            .With(g => g.BBCLeague, "Important Games")
            .Create();
        var dict = new Dictionary<string, List<Game>>
        {
            ["Important Games"] = [ucl, leftover],
        };

        var leagues = svc.GetKnownLeagues(dict);

        Assert.Equal(new[] { "UEFA Champions League" }, leagues);
    }

    [Fact]
    public void FilterGames_UsesDisplayLeague_WhenBbcHasMarriedTheApiBucket()
    {
        var svc = CreateService();
        var games = new List<Game>
        {
            _fixture.Build<Game>()
                .With(g => g.League, "Important Games")
                .With(g => g.BBCLeague, "UEFA Champions League")
                .Create(),
        };

        svc.SetLeagueVisible("UEFA Champions League", false);

        Assert.Empty(svc.FilterGames(games));
    }
}
