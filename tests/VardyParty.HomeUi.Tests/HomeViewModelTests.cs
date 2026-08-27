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

        var sounds = new UiSoundService(new NullUiSoundPlayer(), new InMemorySoundPreferencesStore());
        var menu = new MenuViewModel(_filter.Object, sounds);
        _sut = new HomeViewModel(
            _filter.Object,
            menu,
            _images.Object,
            _assets.Object,
            sounds,
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
    public void FlushPendingApply_EmptyDeliveredCatalog_ShowsEmptyState()
    {
        _sut.UpdateGames(new Dictionary<string, List<Game>>());
        _sut.FlushPendingApply();

        Assert.False(_sut.IsContentLoading);
        Assert.False(_sut.HasGames);
        Assert.True(_sut.ShowEmptyState);
        Assert.Empty(_sut.Rows);
        Assert.Equal(0, _sut.GameCount);
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
