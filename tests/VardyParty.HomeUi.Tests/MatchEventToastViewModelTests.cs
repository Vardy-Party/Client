using System;
using System.Collections.Generic;
using VardyParty.HomeUi;
using VardyParty.Kernel;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public sealed class MatchEventToastViewModelTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly MatchEventToastViewModel _sut;

    public MatchEventToastViewModelTests()
    {
        _sut = new MatchEventToastViewModel(new HomeLayoutState(), _clock);
    }

    [Fact]
    public void Publish_WhenIdle_ShowsImmediately_AndRaisesPresented()
    {
        var presented = new List<MatchEventToastItem>();
        _sut.Presented += presented.Add;
        var item = Item("Jablonec", "Rangers");

        _sut.Publish(item);

        Assert.Same(item, _sut.Current);
        Assert.True(_sut.IsToastVisible);
        Assert.Equal(0, _sut.QueuedCount);
        Assert.Same(item, Assert.Single(presented));
    }

    [Fact]
    public void Publish_WhileShowing_Queues_WithoutReplacingCurrent()
    {
        var first = Item("Alpha", "Beta");
        _sut.Publish(first);

        _sut.Publish(Item("Gamma", "Delta"));

        Assert.Same(first, _sut.Current);
        Assert.Equal(1, _sut.QueuedCount);
    }

    [Fact]
    public void Publish_BeyondQueueCap_DropsOldestQueued()
    {
        _sut.Publish(Item("Showing", "Now"));
        var dropped = Item("Oldest", "Queued");
        _sut.Publish(dropped);
        var kept = new[] { Item("Kept 1", "Q"), Item("Kept 2", "Q"), Item("Kept 3", "Q") };
        foreach (var item in kept)
        {
            _sut.Publish(item);
        }

        Assert.Equal(MatchEventToastViewModel.MaxQueued, _sut.QueuedCount);

        // Drain: the freshest three show, the dropped one never does.
        var shown = new List<MatchEventToastItem>();
        _sut.Presented += shown.Add;
        for (var i = 0; i < 3; i++)
        {
            AdvancePastShowDuration();
            Assert.True(_sut.TryBeginDismiss(_sut.PresentationToken));
            _sut.CompleteDismiss(_sut.PresentationToken);
        }

        Assert.Equal(kept, shown);
        Assert.DoesNotContain(dropped, shown);
    }

    [Fact]
    public void TryBeginDismiss_BeforeShowDuration_Refuses()
    {
        _sut.Publish(Item());
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(_sut.TryBeginDismiss(_sut.PresentationToken));
        Assert.NotNull(_sut.Current);
    }

    [Fact]
    public void TryBeginDismiss_AfterShowDuration_Accepts_Once()
    {
        _sut.Publish(Item());
        AdvancePastShowDuration();

        Assert.True(_sut.TryBeginDismiss(_sut.PresentationToken));

        // The exit animation is already running; a second delayed callback
        // for the same presentation must not start another one.
        Assert.False(_sut.TryBeginDismiss(_sut.PresentationToken));
    }

    [Fact]
    public void TryBeginDismiss_WithinTolerance_Accepts()
    {
        // Platform delayed callbacks can land a few ms early; a refused
        // dismiss is never retried, so the toast would stick forever.
        _sut.Publish(Item());
        _clock.Advance(MatchEventToastViewModel.ShowDuration - TimeSpan.FromMilliseconds(50));

        Assert.True(_sut.TryBeginDismiss(_sut.PresentationToken));
    }

    [Fact]
    public void TryBeginDismiss_StaleToken_Refuses()
    {
        _sut.Publish(Item("First", "Toast"));
        var staleToken = _sut.PresentationToken;
        AdvancePastShowDuration();
        Assert.True(_sut.TryBeginDismiss(staleToken));
        _sut.CompleteDismiss(staleToken);
        _sut.Publish(Item("Second", "Toast"));
        AdvancePastShowDuration();

        // The FIRST toast's second callback must not dismiss the second toast.
        Assert.False(_sut.TryBeginDismiss(staleToken));
        Assert.NotNull(_sut.Current);
    }

    [Fact]
    public void CompleteDismiss_NothingQueued_HidesToast()
    {
        _sut.Publish(Item());
        AdvancePastShowDuration();
        Assert.True(_sut.TryBeginDismiss(_sut.PresentationToken));

        _sut.CompleteDismiss(_sut.PresentationToken);

        Assert.Null(_sut.Current);
        Assert.False(_sut.IsToastVisible);
    }

    [Fact]
    public void CompleteDismiss_PresentsNextQueuedToast_WithFreshToken()
    {
        _sut.Publish(Item("First", "Toast"));
        var next = Item("Second", "Toast");
        _sut.Publish(next);
        var firstToken = _sut.PresentationToken;
        AdvancePastShowDuration();
        Assert.True(_sut.TryBeginDismiss(firstToken));

        _sut.CompleteDismiss(firstToken);

        Assert.Same(next, _sut.Current);
        Assert.Equal(0, _sut.QueuedCount);
        Assert.NotEqual(firstToken, _sut.PresentationToken);

        // The new presentation runs its own full show duration.
        Assert.False(_sut.TryBeginDismiss(_sut.PresentationToken));
        AdvancePastShowDuration();
        Assert.True(_sut.TryBeginDismiss(_sut.PresentationToken));
    }

    [Fact]
    public void CompleteDismiss_StaleToken_IsIgnored()
    {
        _sut.Publish(Item("First", "Toast"));
        var staleToken = _sut.PresentationToken;
        AdvancePastShowDuration();
        Assert.True(_sut.TryBeginDismiss(staleToken));
        _sut.CompleteDismiss(staleToken);
        var current = Item("Second", "Toast");
        _sut.Publish(current);

        _sut.CompleteDismiss(staleToken);

        Assert.Same(current, _sut.Current);
    }

    [Fact]
    public void Item_CarriesHeadlineLeagueAndGameKey()
    {
        var item = Item("Jablonec", "Rangers", homeScore: 2, awayScore: 1);

        Assert.Equal("GOAL — Jablonec 2–1 Rangers", item.Headline);
        Assert.Equal("Cup Alpha", item.LeagueName);
        Assert.Equal("J", item.HomeInitial);
        Assert.Equal("R", item.AwayInitial);
        Assert.False(item.HasLeagueIcon);
        Assert.True(item.NoHomeBadge);
        Assert.True(item.NoAwayBadge);
    }

    private void AdvancePastShowDuration() =>
        _clock.Advance(MatchEventToastViewModel.ShowDuration + TimeSpan.FromMilliseconds(1));

    private static MatchEventToastItem Item(
        string home = "Home United",
        string away = "Away City",
        int homeScore = 1,
        int awayScore = 0)
    {
        var game = new Game
        {
            Home = home,
            Away = away,
            League = "Cup Alpha",
            Start = new DateTime(2026, 8, 27, 19, 0, 0, DateTimeKind.Utc),
            HomeScore = homeScore,
            AwayScore = awayScore,
            IsInProgress = true,
            Minute = 60,
        };
        var matchEvent = new MatchEvent(
            MatchEventKind.Goal, game, homeScore, awayScore, GoalSide.Home);
        return new MatchEventToastItem(matchEvent, leagueIcon: null, homeBadge: null, awayBadge: null);
    }

    /// <summary>Injectable clock: tests advance it explicitly.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }
}
