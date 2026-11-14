using System.Collections.Generic;
using System.ComponentModel;
using VardyParty.HomeUi;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public class LeagueRowViewModelTests
{
    [Fact]
    public void Refresh_UpdatesLeague_AndRaisesPropertyChanged()
    {
        var row = new LeagueRowViewModel(
            "UEFA NATIONS LEAGUE",
            hasLiveGames: true,
            new List<MatchCardViewModel>(),
            new HomeLayoutState());
        string? changed = null;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LeagueRowViewModel.League))
            {
                changed = e.PropertyName;
            }
        };

        row.Refresh(hasLiveGames: true, "UEFA Nations League");

        Assert.Equal("UEFA Nations League", row.League);
        Assert.Equal(nameof(LeagueRowViewModel.League), changed);
    }

    [Fact]
    public void Refresh_SameLeague_DoesNotRaisePropertyChanged()
    {
        var row = new LeagueRowViewModel(
            "UEFA Nations League",
            hasLiveGames: true,
            new List<MatchCardViewModel>(),
            new HomeLayoutState());
        var raised = false;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LeagueRowViewModel.League))
            {
                raised = true;
            }
        };

        row.Refresh(hasLiveGames: false, "UEFA Nations League");

        Assert.Equal("UEFA Nations League", row.League);
        Assert.False(row.HasLiveGames);
        Assert.False(raised);
    }
}
