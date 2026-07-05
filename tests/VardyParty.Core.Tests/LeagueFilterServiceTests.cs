using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Models;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests;

public class LeagueFilterServiceTests
{
  private static LeagueFilterService CreateService(InMemoryLeagueFilterPreferencesStore? store = null)
    {
        return new LeagueFilterService(store ?? new InMemoryLeagueFilterPreferencesStore());
    }

    [Fact]
    public void DefaultHiddenLeagues_AreApplied_WhenNoSavedPreferences()
    {
        var svc = CreateService();

        Assert.False(svc.IsLeagueVisible("NBA"));
        Assert.True(svc.IsLeagueVisible("Premier League"));
    }

    [Fact]
    public void FilterGames_ExcludesHiddenLeagues()
    {
        var svc = CreateService();
        var games = new List<Game>
        {
            new() { League = "Premier League", Home = "A", Away = "B" },
            new() { League = "NBA", Home = "C", Away = "D" }
        };

        var filtered = svc.FilterGames(games);

        Assert.Single(filtered);
        Assert.Equal("Premier League", filtered[0].League);
    }

    [Fact]
    public void SetLeagueVisible_PersistsAndRaisesChanged()
    {
        var store = new InMemoryLeagueFilterPreferencesStore();
        var svc = CreateService(store);
        var changed = false;
        svc.Changed += () => changed = true;

        svc.SetLeagueVisible("NBA", true);

        Assert.True(svc.IsLeagueVisible("NBA"));
        Assert.True(store.HasSavedPreferences);
        Assert.True(changed);
    }

    [Fact]
    public void SetLeaguesVisible_UpdatesMultipleLeaguesOnce()
    {
        var store = new InMemoryLeagueFilterPreferencesStore();
        var svc = CreateService(store);
        var changeCount = 0;
        svc.Changed += () => changeCount++;

        svc.SetLeaguesVisible(new[] { "NBA", "NHL" }, true);

        Assert.True(svc.IsLeagueVisible("NBA"));
        Assert.True(svc.IsLeagueVisible("NHL"));
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void ResetToDefaults_RestoresDefaultHiddenSet()
    {
        var store = new InMemoryLeagueFilterPreferencesStore();
        var svc = CreateService(store);

        svc.SetLeagueVisible("NBA", true);
        svc.ResetToDefaults();

        Assert.False(svc.IsLeagueVisible("NBA"));
        Assert.False(store.HasSavedPreferences);
    }

    [Fact]
    public void GetKnownLeagues_ReturnsSortedLeagueNames()
    {
        var svc = CreateService();
        var dict = new Dictionary<string, List<Game>>
        {
            ["Serie A"] = new(),
            ["Bundesliga"] = new(),
            ["Premier League"] = new()
        };

        var leagues = svc.GetKnownLeagues(dict);

        Assert.Equal(new[] { "Bundesliga", "Premier League", "Serie A" }, leagues);
    }
}
