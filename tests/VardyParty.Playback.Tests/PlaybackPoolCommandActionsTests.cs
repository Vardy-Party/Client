using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutoFixture;
using VardyParty.Models;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Tests;

public class PlaybackPoolCommandActionsTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task AttachCurrentFromPoolAsync_WhenResolvedUrlPresent_AttachesWithoutResolve()
    {
        // Arrange
        var oakLane = CreateOakLane("http://oak-lane.m3u8");
        var (_, actions, attaches, applies, resolved) = CreateSut(oakLane);

        // Act
        await actions.AttachCurrentFromPoolAsync();

        // Assert
        Assert.Equal(new[] { "http://oak-lane.m3u8" }, attaches);
        Assert.Empty(applies);
        Assert.Empty(resolved);
    }

    [Fact]
    public async Task AttachCurrentFromPoolAsync_WhenResolvedUrlMissing_ResolvesThenAttaches()
    {
        // Arrange
        var oakLane = CreateOakLane(resolvedUrl: null);
        oakLane.Referer = "http://northgate.test/oak-lane";
        var (pool, actions, attaches, applies, resolved) = CreateSut(
            oakLane,
            freshUrl: "http://oak-lane-fresh.m3u8");

        // Act
        await actions.AttachCurrentFromPoolAsync();

        // Assert
        Assert.Equal(new[] { "http://northgate.test/oak-lane" }, resolved);
        Assert.Equal("http://oak-lane-fresh.m3u8", oakLane.ResolvedM3U8Url);
        Assert.Equal(new[] { "http://oak-lane-fresh.m3u8" }, attaches);
        Assert.Empty(applies);
        Assert.Same(oakLane, pool.GetCurrentStream());
    }

    [Fact]
    public async Task RetryFreshResolveAsync_WhenFreshUrlDiffers_AttachesFresh()
    {
        // Arrange
        var oakLane = CreateOakLane("http://oak-lane-cached.m3u8");
        var session = new PlaybackSessionController();
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        var (_, actions, attaches, applies, _) = CreateSut(
            oakLane,
            freshUrl: "http://oak-lane-fresh.m3u8",
            session: session);

        // Act
        await actions.RetryFreshResolveAsync();

        // Assert
        Assert.Equal(new[] { "http://oak-lane-fresh.m3u8" }, attaches);
        Assert.Empty(applies);
        Assert.Equal("http://oak-lane-fresh.m3u8", oakLane.ResolvedM3U8Url);
    }

    [Fact]
    public async Task RetryFreshResolveAsync_WhenFreshUrlUnchanged_NotifiesUnavailable()
    {
        // Arrange
        var oakLane = CreateOakLane("http://oak-lane-cached.m3u8");
        var session = new PlaybackSessionController();
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        var (_, actions, attaches, applies, _) = CreateSut(
            oakLane,
            freshUrl: "http://oak-lane-cached.m3u8",
            session: session);

        // Act
        await actions.RetryFreshResolveAsync();

        // Assert
        Assert.Empty(attaches);
        Assert.Single(applies);
        Assert.True(applies[0].CloseSession || applies[0].RemoveCurrentFromPool);
    }

    [Fact]
    public void ClearAndRemove_UpdatePoolAndSessionCount()
    {
        // Arrange
        var oakLane = CreateOakLane("http://oak-lane.m3u8");
        var northgate = CreateOakLane("http://northgate.m3u8");
        northgate.Stream.Channel = "northgate-channel";
        var (pool, actions, _, _, _) = CreateSut(oakLane, extra: northgate);

        // Act
        actions.ClearCurrentResolvedUrl();
        actions.RemoveCurrentFromPool();
        actions.SyncHealthyStreamCount();

        // Assert
        Assert.Null(oakLane.ResolvedM3U8Url);
        Assert.Single(pool.GetHealthyStreams());
        Assert.Equal("northgate-channel", pool.GetCurrentStream()?.Stream.Channel);
    }

    [Fact]
    public async Task RetryFreshResolveAsync_WhenResolveThrows_NotifiesUnavailable()
    {
        // Arrange
        var oakLane = CreateOakLane("http://oak-lane-cached.m3u8");
        var session = new PlaybackSessionController();
        session.BeginAttach("http://oak-lane-cached.m3u8", usedCachedUrl: true);
        var (_, actions, attaches, applies, _) = CreateSut(
            oakLane,
            session: session,
            resolveException: new InvalidOperationException("northgate resolve failed"));

        // Act
        await actions.RetryFreshResolveAsync();

        // Assert
        Assert.Empty(attaches);
        Assert.Single(applies);
        Assert.True(applies[0].CloseSession || applies[0].RemoveCurrentFromPool);
    }

    [Fact]
    public async Task AttachCurrentFromPoolAsync_WhenPoolEmpty_DoesNotAttach()
    {
        // Arrange
        var oakLane = CreateOakLane("http://oak-lane.m3u8");
        var (pool, actions, attaches, applies, _) = CreateSut(oakLane);
        pool.RemoveCurrentStream();

        // Act
        await actions.AttachCurrentFromPoolAsync();

        // Assert
        Assert.Null(pool.GetCurrentStream());
        Assert.Empty(attaches);
        Assert.Empty(applies);
    }

    [Fact]
    public void PlaybackPoolCommandActions_LivesInPlaybackAssembly()
    {
        // Arrange
        var type = typeof(PlaybackPoolCommandActions);

        // Act
        var assemblyName = type.Assembly.GetName().Name;

        // Assert
        Assert.Equal("VardyParty.Playback", assemblyName);
    }

    [Fact]
    public void SwitchPoolToPrevious_MovesCurrentStream()
    {
        // Arrange
        var oakLane = CreateOakLane("http://oak-lane.m3u8");
        var northgate = CreateOakLane("http://northgate.m3u8");
        northgate.Stream.Channel = "northgate-channel";
        var (pool, actions, _, _, _) = CreateSut(oakLane, extra: northgate);
        pool.SwitchToNextStream();

        // Act
        actions.SwitchPoolToPrevious();

        // Assert
        Assert.Equal("oak-lane-channel", pool.GetCurrentStream()?.Stream.Channel);
    }

    [Fact]
    public async Task AttachCurrentFromPoolAsync_WhenResolveReturnsNull_DoesNotAttach()
    {
        // Arrange
        var oakLane = CreateOakLane(resolvedUrl: null);
        var (_, actions, attaches, applies, resolved) = CreateSut(oakLane, freshUrl: null);

        // Act
        await actions.AttachCurrentFromPoolAsync();

        // Assert
        Assert.Single(resolved);
        Assert.Empty(attaches);
        Assert.Empty(applies);
        Assert.Null(oakLane.ResolvedM3U8Url);
    }

    private EnrichedStream CreateOakLane(string? resolvedUrl)
        => _fixture.Build<EnrichedStream>()
            .With(stream => stream.Stream, _fixture.Build<Stream>()
                .With(s => s.Channel, "oak-lane-channel")
                .With(s => s.Url, "http://northgate.test/oak-lane")
                .Create())
            .With(stream => stream.ResolvedM3U8Url, resolvedUrl)
            .With(stream => stream.Status, StreamResolutionStatus.Healthy)
            .With(stream => stream.Referer, "http://northgate.test/oak-lane")
            .Without(stream => stream.Health)
            .Create();

    private (
        StreamSwitchingService Pool,
        PlaybackPoolCommandActions Actions,
        List<string> Attaches,
        List<PlaybackCommand> Applies,
        List<string> ResolvedReferers)
        CreateSut(
            EnrichedStream current,
            EnrichedStream? extra = null,
            string? freshUrl = null,
            PlaybackSessionController? session = null,
            Exception? resolveException = null)
    {
        var pool = new StreamSwitchingService();
        pool.Initialize("northgate-league", "Oak Lane", "Northgate");
        pool.AddHealthyStream(current);
        if (extra != null)
            pool.AddHealthyStream(extra);

        session ??= new PlaybackSessionController();
        session.SetHealthyStreamCount(pool.GetHealthyStreams().Count);

        var attaches = new List<string>();
        var applies = new List<PlaybackCommand>();
        var resolved = new List<string>();

        ResolveFreshPlaybackUrlAsync resolve = (stream, _) =>
        {
            resolved.Add(stream.Referer ?? string.Empty);
            if (resolveException != null)
                return Task.FromException<string?>(resolveException);
            return Task.FromResult(freshUrl);
        };

        var actions = new PlaybackPoolCommandActions(
            session,
            pool,
            resolve,
            attachViaSession: (url, _, _) => attaches.Add(url),
            applyCommand: applies.Add);

        return (pool, actions, attaches, applies, resolved);
    }
}
