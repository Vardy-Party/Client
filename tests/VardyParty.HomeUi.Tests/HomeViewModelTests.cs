using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.Controls;
using Moq;
using VardyParty.Catalog;
using VardyParty.HomeUi;
using VardyParty.Kernel;
using VardyParty.Ports;
using VardyParty.Presentation;
using VardyParty.TestSupport;
using Xunit;

namespace VardyParty.HomeUi.Tests;

public sealed class HomeViewModelTests : IDisposable
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private readonly Mock<ILeagueFilterService> _filter;
    private readonly Mock<IBadgeImageLoader> _images;
    private readonly Mock<IHomeAssetLocator> _assets;
    private readonly MatchEventNotificationPolicy _notifications;
    private readonly MatchEventBus _bus = new();
    private readonly HomeViewModel _sut;

    public HomeViewModelTests()
    {
        _filter = _fixture.GetMock<ILeagueFilterService>();
        _filter
            .Setup(f => f.FilterGames(It.IsAny<IEnumerable<Game>?>()))
            .Returns((IEnumerable<Game>? games) => games?.ToList() ?? new List<Game>());
        _filter
            .Setup(f => f.GetKnownLeagues(It.IsAny<IDictionary<string, List<Game>>?>()))
            .Returns((IDictionary<string, List<Game>>? dict) =>
                dict?.Keys.ToList() ?? new List<string>());
        _filter.Setup(f => f.IsLeagueVisible(It.IsAny<string?>())).Returns(true);

        _images = _fixture.GetMock<IBadgeImageLoader>();
        _images.Setup(i => i.LoadRemoteAsync(It.IsAny<string?>())).ReturnsAsync((ImageSource?)null);
        _images.Setup(i => i.LoadLocalAsync(It.IsAny<string?>())).ReturnsAsync((ImageSource?)null);

        _assets = _fixture.GetMock<IHomeAssetLocator>();
        _assets.Setup(a => a.ResolveLeagueLogoPathAsync(It.IsAny<Game>()))
            .ReturnsAsync((string?)null);

        var preferences = new InMemorySoundPreferencesStore();
        var sounds = new UiSoundService(new NullUiSoundPlayer(), preferences);
        _notifications = new MatchEventNotificationPolicy(preferences);
        var menu = new MenuViewModel(_filter.Object, sounds, _notifications);
        _sut = new HomeViewModel(
            _filter.Object,
            menu,
            _images.Object,
            _assets.Object,
            sounds,
            _notifications,
            _bus,
            NullLogger<HomeViewModel>.Instance);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public void Starts_AsLoading_NotEmpty()
    {
        Assert.True(_sut.IsContentLoading);
        Assert.False(_sut.HasGames);
        Assert.False(_sut.ShowEmptyState);
        Assert.False(_sut.HasPendingWork);
    }

    [Fact]
    public void UpdateGames_WithoutFlush_QueuesWork_AndKeepsLoading()
    {
        var queued = 0;
        _sut.WorkQueued += () => queued++;

        _sut.UpdateGames(CatalogWithOneGame());

        Assert.True(_sut.HasPendingWork);
        Assert.True(_sut.IsContentLoading);
        Assert.False(_sut.ShowEmptyState);
        Assert.Empty(_sut.Rows);
        Assert.True(queued >= 1);
    }

    [Fact]
    public void FlushPendingApply_CoalescesToLatestQueuedCatalog()
    {
        _sut.UpdateGames(CatalogWithOneGame("Alpha"));
        _sut.UpdateGames(CatalogWithOneGame("Beta"));
        _sut.FlushPendingApply();

        Assert.Equal("Beta", _sut.Rows[0].Cards[0].HomeTeam);
        Assert.False(_sut.HasPendingWork);
    }

    [Fact]
    public void FlushPendingApply_FirstCatalog_StopsLoading_AndRaisesGamesUpdated()
    {
        var updated = 0;
        _sut.GamesUpdated += count => updated = count;

        _sut.UpdateGames(CatalogWithOneGame());
        _sut.FlushPendingApply();

        Assert.False(_sut.IsContentLoading);
        Assert.True(_sut.HasGames);
        Assert.False(_sut.ShowEmptyState);
        Assert.False(_sut.HasPendingWork);
        Assert.Equal(1, updated);
        Assert.Equal(1, _sut.GameCount);
        Assert.Single(_sut.Rows);
        Assert.Single(_sut.Rows[0].Cards);
        Assert.True(_sut.Rows[0].Cards[0].RequestsInitialFocus);
    }

    [Fact]
    public void FlushPendingApply_EmptyCatalog_StaysLoading_NeverSettles()
    {
        // Arrange & Act: an apply WITHOUT API data (empty pre-API board) must
        // not settle the crest or flip the subtitle — "ready" strictly means
        // API games are present (the enriched-first feed guarantees the real
        // initial board is never empty-as-settled).
        _sut.UpdateGames(new Dictionary<string, List<Game>>());
        _sut.FlushPendingApply();

        // Assert
        Assert.True(_sut.IsContentLoading);
        Assert.False(_sut.HasGames);
        Assert.False(_sut.ShowEmptyState);
        Assert.Empty(_sut.Rows);
        Assert.Equal(0, _sut.GameCount);
    }

    [Fact]
    public void FlushPendingApply_AllLeaguesFilteredOut_IsReady_ShowsEmptyState()
    {
        // Arrange: the board HAS API data but the user hid every league — that
        // is a delivered, settled board, so the empty state (not the spinner)
        // must show.
        _filter
            .Setup(f => f.FilterGames(It.IsAny<IEnumerable<Game>?>()))
            .Returns(new List<Game>());
        _sut.UpdateGames(CatalogWithOneGame());

        // Act
        _sut.FlushPendingApply();

        // Assert
        Assert.False(_sut.IsContentLoading);
        Assert.False(_sut.HasGames);
        Assert.True(_sut.ShowEmptyState);
        Assert.Equal("0 games", _sut.Subtitle);
    }

    [Fact]
    public void FlushPendingApply_NullCatalog_IsLoadingAgain()
    {
        _sut.UpdateGames(CatalogWithOneGame());
        _sut.FlushPendingApply();

        _sut.UpdateGames(null);
        _sut.FlushPendingApply();

        Assert.True(_sut.IsContentLoading);
        Assert.False(_sut.HasGames);
        Assert.False(_sut.ShowEmptyState);
        Assert.Empty(_sut.Rows);
    }

    [Fact]
    public void FlushPendingApply_SecondCatalog_DoesNotReArmInitialFocus()
    {
        _sut.UpdateGames(CatalogWithOneGame("Alpha"));
        _sut.FlushPendingApply();
        _sut.Rows[0].Cards[0].TryConsumeInitialFocus();

        _sut.UpdateGames(CatalogWithOneGame("Beta"));
        _sut.FlushPendingApply();

        Assert.False(_sut.Rows[0].Cards[0].RequestsInitialFocus);
    }

    [Fact]
    public void Subtitle_StartsAsLoading_NeverZeroGames()
    {
        // Arrange: construction only — no catalog delivered yet.

        // Act
        var subtitle = _sut.Subtitle;

        // Assert
        Assert.Equal(HomeViewModel.LoadingSubtitle, subtitle);
    }

    [Fact]
    public void Subtitle_NullCatalogWhileLoading_StaysLoading()
    {
        // Arrange: the games feed is a BehaviorSubject seeded with null, so
        // subscribing delivers a null board before the first real catalog —
        // this used to render "0 games" under the spinning crest.
        _sut.UpdateGames(null);

        // Act
        _sut.FlushPendingApply();

        // Assert
        Assert.True(_sut.IsContentLoading);
        Assert.Equal(HomeViewModel.LoadingSubtitle, _sut.Subtitle);
    }

    [Fact]
    public void Subtitle_FirstCatalog_ShowsGameCount()
    {
        // Arrange: the startup null emit, then the first real catalog.
        _sut.UpdateGames(null);
        _sut.FlushPendingApply();
        _sut.UpdateGames(CatalogWithUpcomingGames(1));

        // Act
        _sut.FlushPendingApply();

        // Assert
        Assert.False(_sut.IsContentLoading);
        Assert.Equal("1 game", _sut.Subtitle);
    }

    [Fact]
    public void Subtitle_Refresh_ShowsCountsAndLiveGames()
    {
        // Arrange: first catalog applied, then a refresh adds a live game.
        _sut.UpdateGames(CatalogWithUpcomingGames(1));
        _sut.FlushPendingApply();
        _sut.UpdateGames(CatalogWithUpcomingGames(2, liveGames: 1));

        // Act
        _sut.FlushPendingApply();

        // Assert
        Assert.Equal("3 games · 1 live", _sut.Subtitle);
    }

    [Fact]
    public void Subtitle_EmptyCatalog_StaysLoading()
    {
        // Arrange: an empty board is a pre-API artifact under the enriched-
        // first contract — the subtitle must keep reading Loading…, never
        // "0 games".
        _sut.UpdateGames(new Dictionary<string, List<Game>>());

        // Act
        _sut.FlushPendingApply();

        // Assert
        Assert.True(_sut.IsContentLoading);
        Assert.Equal(HomeViewModel.LoadingSubtitle, _sut.Subtitle);
    }

    [Fact]
    public void Subtitle_SignOutNullCatalog_ReturnsToLoading()
    {
        // Arrange: a real catalog applied, then sign-out clears the feed.
        _sut.UpdateGames(CatalogWithUpcomingGames(1));
        _sut.FlushPendingApply();
        _sut.UpdateGames(null);

        // Act
        _sut.FlushPendingApply();

        // Assert
        Assert.True(_sut.IsContentLoading);
        Assert.Equal(HomeViewModel.LoadingSubtitle, _sut.Subtitle);
    }

    [Fact]
    public void LeagueToggles_FillOnlyWhenMenuIsOpen()
    {
        _sut.UpdateGames(CatalogWithOneGame());
        _sut.FlushPendingApply();
        Assert.Empty(_sut.LeagueToggles);

        _sut.ToggleMenu();

        Assert.True(_sut.IsMenuOpen);
        Assert.Contains(_sut.LeagueToggles, t => t.Name == "League Alpha");
    }

    [Fact]
    public void SetError_WithoutFlush_IsPending_ThenFlushAppliesBanner()
    {
        _sut.SetError("catalog down");
        Assert.True(_sut.HasPendingWork);
        Assert.False(_sut.HasError);

        _sut.FlushPendingApply();

        Assert.True(_sut.HasError);
        Assert.Equal("catalog down", _sut.ErrorMessage);
        Assert.False(_sut.HasPendingWork);
    }

    [Fact]
    public void OnStreamResolutionEnded_ClearsResolvingOnFlush()
    {
        _sut.UpdateGames(CatalogWithOneGame());
        _sut.FlushPendingApply();
        _sut.Rows[0].Cards[0].Pick();
        Assert.True(_sut.Rows[0].Cards[0].IsResolving);

        _sut.OnStreamResolutionEnded();
        Assert.True(_sut.Rows[0].Cards[0].IsResolving);

        _sut.FlushPendingApply();
        Assert.False(_sut.Rows[0].Cards[0].IsResolving);
        Assert.False(_sut.HasPendingWork);
    }

    [Fact]
    public async Task LoadImagesAsync_StaleEpoch_DoesNotPaintDiscardedCards()
    {
        var gate = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var image = new FileImageSource { File = "stale-badge.png" };
        _images.Setup(i => i.LoadRemoteAsync(It.IsAny<string?>())).Returns(gate.Task);

        _sut.UpdateGames(CatalogWithOneGame("Alpha"));
        _sut.FlushPendingApply();
        var staleCard = _sut.Rows[0].Cards[0];

        await WaitUntilAsync(() =>
            _images.Invocations.Any(c => c.Method.Name == nameof(IBadgeImageLoader.LoadRemoteAsync)));

        _sut.UpdateGames(CatalogWithOneGame("Beta"));
        _sut.FlushPendingApply();
        var liveCard = _sut.Rows[0].Cards[0];

        gate.SetResult(image);
        await WaitUntilAsync(() => _sut.HasPendingWork);
        _sut.FlushPendingApply();

        Assert.Null(staleCard.HomeBadge);
        Assert.NotNull(liveCard.HomeBadge);
    }

    [Fact]
    public void FlushPendingApply_Refresh_UpdatesCardsInPlace_SameInstances()
    {
        // Arrange: same fixture, fresh Game instance with a new score.
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 0, awayScore: 0, minute: 10));
        _sut.FlushPendingApply();
        var row = _sut.Rows[0];
        var card = row.Cards[0];

        // Act
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 1, awayScore: 0, minute: 23));
        _sut.FlushPendingApply();

        // Assert: no re-materialization — the SAME row and card instances,
        // with updated INPC properties.
        Assert.Same(row, _sut.Rows[0]);
        Assert.Same(card, _sut.Rows[0].Cards[0]);
        Assert.Equal("1 - 0", card.ScoreText);
        Assert.Contains("23", card.StatusText);
    }

    [Fact]
    public void FlushPendingApply_Refresh_PreservesLoadedBadgesAndLeagueIcon()
    {
        // Arrange: a badge already loaded on the card, an icon on the row.
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 0, awayScore: 0, minute: 10));
        _sut.FlushPendingApply();
        var badge = new FileImageSource { File = "loaded-badge.png" };
        var icon = new FileImageSource { File = "league-icon.png" };
        _sut.Rows[0].Cards[0].HomeBadge = badge;
        _sut.Rows[0].LeagueIcon = icon;

        // Act: the same fixture refreshes (same badge URLs).
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 2, awayScore: 1, minute: 60));
        _sut.FlushPendingApply();

        // Assert: images survive the poll — nothing re-decodes or re-assigns.
        Assert.Same(badge, _sut.Rows[0].Cards[0].HomeBadge);
        Assert.Same(icon, _sut.Rows[0].LeagueIcon);
    }

    [Fact]
    public void FlushPendingApply_Refresh_RemovesGoneCards_AddsNewOnes()
    {
        // Arrange: two fixtures shown.
        _sut.UpdateGames(CatalogWithFixtures("League Alpha", ("Home United", "Away City"), ("North Rovers", "South Wanderers")));
        _sut.FlushPendingApply();
        var keptCard = _sut.Rows[0].Cards[0];

        // Act: the second fixture is gone; a third appears.
        _sut.UpdateGames(CatalogWithFixtures("League Alpha", ("Home United", "Away City"), ("East Athletic", "West Albion")));
        _sut.FlushPendingApply();

        // Assert
        Assert.Equal(2, _sut.Rows[0].Cards.Count);
        Assert.Same(keptCard, _sut.Rows[0].Cards[0]);
        Assert.Equal("East Athletic", _sut.Rows[0].Cards[1].HomeTeam);
        Assert.Equal("2 matches", _sut.Rows[0].MatchCountText);
    }

    [Fact]
    public void FlushPendingApply_Refresh_FocusedRowNeverMoves()
    {
        // Arrange: two leagues shown, focus lands in the first row; the next
        // poll makes the OTHER league live (a re-tier that would put it on top).
        _sut.UpdateGames(TwoLeagueCatalog(betaLive: false));
        _sut.FlushPendingApply();
        Assert.Equal("League Alpha", _sut.Rows[0].League);
        _sut.Rows[0].Cards[0].FocusMoved();

        // Act
        _sut.UpdateGames(TwoLeagueCatalog(betaLive: true));
        _sut.FlushPendingApply();

        // Assert: the focused row holds its position.
        Assert.Equal("League Alpha", _sut.Rows[0].League);
        Assert.Equal("Cup Beta", _sut.Rows[1].League);
        Assert.True(_sut.Rows[1].HasLiveGames);
    }

    [Fact]
    public void FlushPendingApply_Refresh_UnfocusedReTier_UsesLiveFirstOrder()
    {
        // Arrange: same transition as above but nothing is focused — the
        // live-set change re-tiers to the builder's live-first order.
        _sut.UpdateGames(TwoLeagueCatalog(betaLive: false));
        _sut.FlushPendingApply();

        // Act
        _sut.UpdateGames(TwoLeagueCatalog(betaLive: true));
        _sut.FlushPendingApply();

        // Assert
        Assert.Equal("Cup Beta", _sut.Rows[0].League);
    }

    [Fact]
    public void FlushPendingApply_TvWideStrip_StagesCardsBeyondBudget()
    {
        // Arrange: TV class stages strips (BindableLayout materializes every
        // card in a row at bind time — a wide row must not become one huge
        // layout pass on the weak core).
        _sut.Layout.Apply(HomeLayoutClass.Tv);

        // Act: 12 fixtures in one league, budget is 8.
        _sut.UpdateGames(CatalogWithUpcomingGames(12));
        _sut.FlushPendingApply();

        // Assert: first chunk shown, remainder owed; initial focus is armed
        // on a materialized card.
        Assert.Equal(_sut.Layout.StagedStripCards, _sut.Rows[0].Cards.Count);
        Assert.True(_sut.HasStagedStripWork);
        Assert.True(_sut.Rows[0].Cards[0].RequestsInitialFocus);

        // Act: drain the staged chunks like the view's dispatcher pump does.
        var guard = 0;
        while (_sut.MaterializeNextStagedStripChunk() && ++guard < 10)
        {
        }

        // Assert: the full board materialized exactly once per game.
        Assert.Equal(12, _sut.Rows[0].Cards.Count);
        Assert.Equal(12, _sut.Rows[0].Cards.Select(c => HomeBoardDiffer.GameKey(c.Game)).Distinct().Count());
        Assert.False(_sut.HasStagedStripWork);
    }

    [Fact]
    public void FlushPendingApply_NonTv_MaterializesTheFullStripImmediately()
    {
        // Desktop/phone have no staging budget: the strip is complete at bind.
        _sut.UpdateGames(CatalogWithUpcomingGames(12));
        _sut.FlushPendingApply();

        Assert.Equal(12, _sut.Rows[0].Cards.Count);
        Assert.False(_sut.HasStagedStripWork);
        Assert.False(_sut.MaterializeNextStagedStripChunk());
    }

    [Fact]
    public void FlushPendingApply_NewApplySupersedesStagedWork_NoDuplicates()
    {
        // Arrange: staged work is pending when the next poll lands.
        _sut.Layout.Apply(HomeLayoutClass.Tv);
        _sut.UpdateGames(CatalogWithUpcomingGames(12));
        _sut.FlushPendingApply();
        Assert.True(_sut.HasStagedStripWork);

        // Act: the new apply's diff plans against the FULL board, so it
        // inserts the cards the stale staged entries still owed.
        _sut.UpdateGames(CatalogWithUpcomingGames(12));
        _sut.FlushPendingApply();

        // Assert: complete, no duplicates, stale staged entries dropped.
        Assert.Equal(12, _sut.Rows[0].Cards.Count);
        Assert.Equal(12, _sut.Rows[0].Cards.Select(c => HomeBoardDiffer.GameKey(c.Game)).Distinct().Count());
        Assert.False(_sut.HasStagedStripWork);
        Assert.False(_sut.MaterializeNextStagedStripChunk());
    }

    [Fact]
    public void GoalNotificationsEnabled_DefaultsOn_TogglePersistsToPolicy()
    {
        Assert.True(_sut.GoalNotificationsEnabled);

        _sut.GoalNotificationsEnabled = false;

        Assert.False(_sut.GoalNotificationsEnabled);
        Assert.False(_notifications.NotificationsEnabled);
    }

    [Fact]
    public void FlushPendingApply_Goal_PublishesToastAndBusEvent()
    {
        // Arrange: the fixture observed once, then its score moves.
        var delivered = new List<MatchEvent>();
        _bus.Published += delivered.Add;
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 0, awayScore: 0, minute: 10));
        _sut.FlushPendingApply();

        // Act
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 1, awayScore: 0, minute: 23));
        _sut.FlushPendingApply();

        // Assert: one delivered event on the bus and a showing toast whose
        // headline attributes the sting ("3 notes, no idea what it means").
        var matchEvent = Assert.Single(delivered);
        Assert.Equal(MatchEventKind.Goal, matchEvent.Kind);
        Assert.NotNull(_sut.Toast.Current);
        Assert.Equal("GOAL — Home United 1–0 Away City", _sut.Toast.Current!.Headline);
        Assert.Equal("League Alpha", _sut.Toast.Current.LeagueName);
    }

    [Fact]
    public void FlushPendingApply_Goal_FlashesTheMaterializedCard()
    {
        // Arrange
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 0, awayScore: 0, minute: 10));
        _sut.FlushPendingApply();
        var flashes = 0;
        _sut.Rows[0].Cards[0].FlashRequested += () => flashes++;

        // Act
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 1, awayScore: 0, minute: 23));
        _sut.FlushPendingApply();

        // Assert: one flash, synchronized with the toast delivery.
        Assert.Equal(1, flashes);
        Assert.NotNull(_sut.Toast.Current);
    }

    [Fact]
    public void FlushPendingApply_Goal_NotificationsOff_DeliversNothing()
    {
        // Arrange
        var delivered = 0;
        _bus.Published += _ => delivered++;
        _notifications.SetNotificationsEnabled(false);
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 0, awayScore: 0, minute: 10));
        _sut.FlushPendingApply();
        var flashes = 0;
        _sut.Rows[0].Cards[0].FlashRequested += () => flashes++;

        // Act
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 1, awayScore: 0, minute: 23));
        _sut.FlushPendingApply();

        // Assert: OFF suppresses sting + toast + card flash entirely.
        Assert.Equal(0, delivered);
        Assert.Equal(0, flashes);
        Assert.Null(_sut.Toast.Current);
    }

    [Fact]
    public void FlushPendingApply_Goal_Backgrounded_DroppedNotQueued()
    {
        // Arrange
        var delivered = 0;
        _bus.Published += _ => delivered++;
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 0, awayScore: 0, minute: 10));
        _sut.FlushPendingApply();
        _notifications.IsAppForegrounded = false;

        // Act: the goal lands while the app is backgrounded, then the app
        // resumes and a scoreless refresh applies.
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 1, awayScore: 0, minute: 23));
        _sut.FlushPendingApply();
        _notifications.IsAppForegrounded = true;
        _sut.UpdateGames(CatalogWithScoredGame(homeScore: 1, awayScore: 0, minute: 24));
        _sut.FlushPendingApply();

        // Assert: nothing at all — no catch-up toast on resume.
        Assert.Equal(0, delivered);
        Assert.Null(_sut.Toast.Current);
    }

    private Dictionary<string, List<Game>> CatalogWithScoredGame(int homeScore, int awayScore, int minute)
    {
        var game = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, "")
            .With(g => g.StatusText, "")
            .With(g => g.IsFinished, false)
            .With(g => g.IsInProgress, true)
            .With(g => g.IsHalfTime, false)
            .With(g => g.Minute, (int?)minute)
            .With(g => g.HomeScore, (int?)homeScore)
            .With(g => g.AwayScore, (int?)awayScore)
            // Fixed kickoff: the event detector keys on Home|Away|Start, so
            // refreshes of the same fixture must carry the same Start.
            .With(g => g.Start, DateTime.UtcNow.Date.AddHours(-1))
            .With(g => g.HomeBadgeUrl, "https://example.test/home.svg")
            .With(g => g.AwayBadgeUrl, "https://example.test/away.svg")
            .Create();

        return new Dictionary<string, List<Game>>
        {
            ["League Alpha"] = [game],
        };
    }

    private Dictionary<string, List<Game>> CatalogWithFixtures(string league, params (string Home, string Away)[] fixtures)
    {
        var games = fixtures.Select(f => _fixture.Build<Game>()
            .With(g => g.Home, f.Home)
            .With(g => g.Away, f.Away)
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.League, league)
            .With(g => g.BBCLeague, "")
            .With(g => g.StatusText, "")
            .With(g => g.IsFinished, false)
            .With(g => g.IsInProgress, false)
            .With(g => g.IsHalfTime, false)
            .With(g => g.Minute, (int?)null)
            .With(g => g.Start, DateTime.UtcNow.AddHours(2))
            .With(g => g.HomeBadgeUrl, "https://example.test/home.svg")
            .With(g => g.AwayBadgeUrl, "https://example.test/away.svg")
            .Create()).ToList();

        return new Dictionary<string, List<Game>>
        {
            [league] = games,
        };
    }

    private Dictionary<string, List<Game>> TwoLeagueCatalog(bool betaLive)
    {
        var alpha = _fixture.Build<Game>()
            .With(g => g.Home, "Home United")
            .With(g => g.Away, "Away City")
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, "")
            .With(g => g.StatusText, "")
            .With(g => g.IsFinished, false)
            .With(g => g.IsInProgress, false)
            .With(g => g.IsHalfTime, false)
            .With(g => g.Minute, (int?)null)
            .With(g => g.Start, DateTime.UtcNow.AddHours(1))
            .With(g => g.HomeBadgeUrl, "https://example.test/home.svg")
            .With(g => g.AwayBadgeUrl, "https://example.test/away.svg")
            .Create();

        var beta = _fixture.Build<Game>()
            .With(g => g.Home, "River Town")
            .With(g => g.Away, "Lake Borough")
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.League, "Cup Beta")
            .With(g => g.BBCLeague, "")
            .With(g => g.StatusText, "")
            .With(g => g.IsFinished, false)
            .With(g => g.IsInProgress, betaLive)
            .With(g => g.IsHalfTime, false)
            .With(g => g.Minute, betaLive ? 12 : (int?)null)
            .With(g => g.Start, betaLive ? DateTime.UtcNow.AddMinutes(-12) : DateTime.UtcNow.AddHours(2))
            .With(g => g.HomeBadgeUrl, "https://example.test/home.svg")
            .With(g => g.AwayBadgeUrl, "https://example.test/away.svg")
            .Create();

        return new Dictionary<string, List<Game>>
        {
            ["League Alpha"] = [alpha],
            ["Cup Beta"] = [beta],
        };
    }

    private Dictionary<string, List<Game>> CatalogWithOneGame(string home = "Home United")
    {
        var game = _fixture.Build<Game>()
            .With(g => g.Home, home)
            .With(g => g.Away, "Away City")
            .With(g => g.BBCHome, "")
            .With(g => g.BBCAway, "")
            .With(g => g.League, "League Alpha")
            .With(g => g.BBCLeague, "")
            .With(g => g.IsFinished, false)
            .With(g => g.IsInProgress, false)
            .With(g => g.Start, DateTime.UtcNow.AddHours(2))
            .With(g => g.HomeBadgeUrl, "https://example.test/home.svg")
            .With(g => g.AwayBadgeUrl, "https://example.test/away.svg")
            .Create();

        return new Dictionary<string, List<Game>>
        {
            ["League Alpha"] = [game],
        };
    }

    private Dictionary<string, List<Game>> CatalogWithUpcomingGames(int upcomingGames, int liveGames = 0)
    {
        var games = new List<Game>();
        for (var i = 0; i < upcomingGames; i++)
        {
            games.Add(_fixture.Build<Game>()
                .With(g => g.Home, $"Home United {i}")
                .With(g => g.Away, $"Away City {i}")
                .With(g => g.BBCHome, "")
                .With(g => g.BBCAway, "")
                .With(g => g.League, "League Alpha")
                .With(g => g.BBCLeague, "")
                .With(g => g.StatusText, "")
                .With(g => g.IsFinished, false)
                .With(g => g.IsInProgress, false)
                .With(g => g.IsHalfTime, false)
                .With(g => g.Minute, (int?)null)
                .With(g => g.Start, DateTime.UtcNow.AddHours(2))
                .With(g => g.HomeBadgeUrl, "https://example.test/home.svg")
                .With(g => g.AwayBadgeUrl, "https://example.test/away.svg")
                .Create());
        }

        for (var i = 0; i < liveGames; i++)
        {
            games.Add(_fixture.Build<Game>()
                .With(g => g.Home, $"Live Rovers {i}")
                .With(g => g.Away, $"Live Wanderers {i}")
                .With(g => g.BBCHome, "")
                .With(g => g.BBCAway, "")
                .With(g => g.League, "League Alpha")
                .With(g => g.BBCLeague, "")
                .With(g => g.StatusText, "")
                .With(g => g.IsFinished, false)
                .With(g => g.IsInProgress, true)
                .With(g => g.IsHalfTime, false)
                .With(g => g.Minute, (int?)30)
                .With(g => g.Start, DateTime.UtcNow.AddMinutes(-30))
                .With(g => g.HomeBadgeUrl, "https://example.test/home.svg")
                .With(g => g.AwayBadgeUrl, "https://example.test/away.svg")
                .Create());
        }

        return new Dictionary<string, List<Game>>
        {
            ["League Alpha"] = games,
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Timed out waiting for image load to start.");
    }
}
