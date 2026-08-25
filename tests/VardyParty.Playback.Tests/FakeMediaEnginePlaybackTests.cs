using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using VardyParty.Models;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Tests;

/// <summary>
/// Business tests of stream handling through <see cref="IMediaEngine"/> — the seam OS adapters
/// should implement. ExoPlayer/WinUI stay out of Core tests; a fake engine is enough.
/// </summary>
public class FakeMediaEnginePlaybackTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task FakeEngine_Ready_EstablishesSession()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var (session, _, engine, host) = CreateHost(current, next);
        host.AttachFromPool();

        // Act
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Assert
        Assert.Equal(current.Url, engine.LastAttached);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.Equal(current.Url, session.Snapshot.LastGoodUrl);
        Assert.Single(engine.AttachLog);
        await Task.CompletedTask;
    }

    [Fact]
    public void FakeEngine_ErrorWhilePlaying_AttachesNextPoolEntryWithoutSkipping()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var skipped = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(current, next, skipped);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Act
        engine.RaiseError(session.Snapshot.AttachGeneration, _fixture.Create<string>());

        // Assert
        Assert.Equal(next.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal(next.Url, engine.LastAttached);
        Assert.DoesNotContain(skipped.Url, engine.AttachLog);
    }

    [Fact]
    public void FakeEngine_ErrorWhileSwitching_ReattachesLastGood()
    {
        // Arrange
        var lastGood = _fixture.Create<Stream>();
        var failedSwitch = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(lastGood, failedSwitch);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);
        pool.SwitchToNextStream();
        host.AttachFromPool();

        // Act
        engine.RaiseError(session.Snapshot.AttachGeneration, _fixture.Create<string>());

        // Assert
        Assert.Equal(lastGood.Url, engine.LastAttached);
        Assert.Equal(lastGood.Url, session.Snapshot.CurrentUrl);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void FakeEngine_UserNext_DoesNotRemove_AttachesNext()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(current, next);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Act
        host.Dispatch(MediaEngineEvent.UserNext());

        // Assert
        Assert.Equal(2, pool.GetHealthyStreams().Count);
        Assert.Equal(next.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal(next.Url, engine.LastAttached);
    }

    [Fact]
    public void FakeEngine_ClearedError_IsNotRaised_NoSwitch()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(current, next);
        host.AttachFromPool();

        // Act
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Assert
        Assert.True(PlaybackPolicy.ShouldIgnoreClearedEngineError(true));
        Assert.Equal(current.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
        Assert.Single(engine.AttachLog);
    }

    [Fact]
    public void FakeEngine_FailedStart_AttachesNextWithoutSkipping()
    {
        // Arrange
        var failedStart = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var skipped = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(failedStart, next, skipped);
        host.AttachFromPool();

        // Act
        engine.RaiseError(session.Snapshot.AttachGeneration, _fixture.Create<string>());

        // Assert
        Assert.Equal(next.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal(next.Url, engine.LastAttached);
        Assert.DoesNotContain(skipped.Url, engine.AttachLog);
    }

    [Fact]
    public void FakeEngine_PlaybackEnded_DoesNotAttachNext()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(current, next);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Act
        engine.RaiseEnded(session.Snapshot.AttachGeneration);

        // Assert
        Assert.Equal(current.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Single(engine.AttachLog);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void FakeEngine_LastStreamHardFail_ClosesWithoutAttach()
    {
        // Arrange
        var only = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(only);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Act
        engine.RaiseError(session.Snapshot.AttachGeneration, _fixture.Create<string>());

        // Assert
        Assert.Null(pool.GetCurrentStream());
        Assert.Empty(pool.GetHealthyStreams());
        Assert.Single(engine.AttachLog);
        Assert.Equal(PlaybackSessionState.Failed, session.Snapshot.State);
    }

    private (PlaybackSessionController session, StreamSwitchingService pool, FakeMediaEngine engine, EngineHost host)
        CreateHost(params Stream[] streams)
    {
        var pool = _fixture.Create<StreamSwitchingService>();
        pool.Initialize(_fixture.Create<string>(), _fixture.Create<string>(), _fixture.Create<string>());
        foreach (var stream in streams)
        {
            pool.AddHealthyStream(_fixture.Build<EnrichedStream>()
                .With(e => e.Stream, stream)
                .With(e => e.ResolvedM3U8Url, stream.Url)
                .With(e => e.Status, StreamResolutionStatus.Healthy)
                .Without(e => e.Health)
                .Create());
        }

        var session = _fixture.Create<PlaybackSessionController>();
        session.SetHealthyStreamCount(pool.GetHealthyStreams().Count);
        var engine = new FakeMediaEngine();
        var host = new EngineHost(session, pool, engine);
        engine.EngineEvent += (_, e) => host.Dispatch(e);
        return (session, pool, engine, host);
    }

    private sealed class FakeMediaEngine : IMediaEngine
    {
        public event EventHandler<MediaEngineEvent>? EngineEvent;
        public List<string> AttachLog { get; } = [];
        public string? LastAttached => AttachLog.Count == 0 ? null : AttachLog[^1];

        public Task AttachAsync(
            string mediaUrl,
            IReadOnlyDictionary<string, string>? requestHeaders = null,
            CancellationToken cancellationToken = default)
        {
            AttachLog.Add(mediaUrl);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public PlaybackMetrics? GetCurrentMetrics() => null;

        public void RaiseReady(long generation) =>
            EngineEvent?.Invoke(this, MediaEngineEvent.Ready(generation));

        public void RaiseError(long generation, string message) =>
            EngineEvent?.Invoke(this, MediaEngineEvent.Error(generation, message));

        public void RaiseEnded(long generation) =>
            EngineEvent?.Invoke(this, MediaEngineEvent.Ended(generation));
    }

    private sealed class EngineHost(
        PlaybackSessionController session,
        StreamSwitchingService pool,
        FakeMediaEngine engine)
    {
        public void AttachFromPool()
        {
            var url = pool.GetCurrentStream()?.ResolvedM3U8Url;
            if (string.IsNullOrWhiteSpace(url)) return;
            Apply(session.BeginAttach(url, usedCachedUrl: false));
        }

        public void Dispatch(MediaEngineEvent e) => Apply(session.Handle(e));

        private void Apply(IReadOnlyList<PlaybackEffect> effects)
        {
            var cmd = PlaybackCommand.FromEffects(effects);
            if (cmd.IsNoOp) return;

            if (cmd.ClearResolvedUrl)
            {
                var current = pool.GetCurrentStream();
                if (current != null) current.ResolvedM3U8Url = null;
            }

            if (cmd.RemoveCurrentFromPool)
                pool.RemoveCurrentStream();

            session.SetHealthyStreamCount(pool.GetHealthyStreams().Count);

            if (!string.IsNullOrWhiteSpace(cmd.AttachUrl))
            {
                engine.AttachAsync(cmd.AttachUrl).GetAwaiter().GetResult();
            }
            else if (cmd.AttachCurrentAfterRemove)
            {
                var url = pool.GetCurrentStream()?.ResolvedM3U8Url;
                if (!string.IsNullOrWhiteSpace(url))
                    Apply(session.BeginAttach(url, usedCachedUrl: false, force: true));
            }
            else if (cmd.SwitchPoolToNext)
            {
                pool.SwitchToNextStream();
                AttachFromPool();
            }
            else if (cmd.SwitchPoolToPrevious)
            {
                pool.SwitchToPreviousStream();
                AttachFromPool();
            }
        }
    }
}
