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
        // Arrange
        var row = new LeagueRowViewModel(
            "ZFA NORTHERN CUP",
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

        // Act
        row.Refresh(hasLiveGames: true, "ZFA Northern Cup");

        // Assert
        Assert.Equal("ZFA Northern Cup", row.League);
        Assert.Equal(nameof(LeagueRowViewModel.League), changed);
    }

    [Fact]
    public void Refresh_SameLeague_DoesNotRaisePropertyChanged()
    {
        // Arrange
        var row = new LeagueRowViewModel(
            "ZFA Northern Cup",
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

        // Act
        row.Refresh(hasLiveGames: false, "ZFA Northern Cup");

        // Assert
        Assert.Equal("ZFA Northern Cup", row.League);
        Assert.False(row.HasLiveGames);
        Assert.False(raised);
    }
}
