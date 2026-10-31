using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Playback.Tests;

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
    public void FakeEngine_TransientErrorWhilePlaying_ReloadsSameStreamAndKeepsPool()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(current, next);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Act: a network blip on the live playlist.
        engine.RaiseError(session.Snapshot.AttachGeneration, "Source error: HttpDataSourceException timeout");
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Assert: same row, same URL re-attached, nothing removed, playing again.
        Assert.Equal(current.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal(new[] { current.Url, current.Url }, engine.AttachLog);
        Assert.Equal(2, pool.GetHealthyStreams().Count);
        Assert.Empty(host.Failed);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void FakeEngine_PlaylistRejected_FreshPlaySameStreamBeforeSwitching()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var resolves = 0;
        var (session, pool, engine, host) = CreateHost(
            (_, _) => Task.FromResult<string?>($"http://fresh-{++resolves}.m3u8"),
            current, next);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Act: the token expired (HTML instead of #EXTM3U), then the fresh URL plays.
        engine.RaiseError(session.Snapshot.AttachGeneration, "Input does not start with the #EXTM3U header");
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Assert
        Assert.Equal(1, resolves);
        Assert.Equal(current.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal("http://fresh-1.m3u8", engine.LastAttached);
        Assert.Empty(host.Failed);
        Assert.Equal(PlaybackSessionState.Playing, session.Snapshot.State);
    }

    [Fact]
    public void FakeEngine_RetriesExhausted_ReportsOnceAndSwitchesToNext()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var resolves = 0;
        var (session, pool, engine, host) = CreateHost(
            (s, _) =>
            {
                if (s.Stream.Channel == current.Channel)
                    resolves++;
                return Task.FromResult<string?>($"http://fresh-{resolves}-{s.Stream.Channel}.m3u8");
            },
            current, next);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Act: the stream keeps failing through every retry.
        for (var i = 0; i <= PlaybackPolicy.MaxSameStreamRetries; i++)
            engine.RaiseError(session.Snapshot.AttachGeneration, "Source error: stalled");

        // Assert: reload, then two fresh /play calls, then exactly one failed report and a switch.
        Assert.Equal(2, resolves);
        Assert.Single(host.Failed);
        Assert.Equal(next.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Contains(next.Channel, engine.LastAttached ?? next.Url);
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

        // Act: a confirmed-dead error (CDN host gone) switches at once; a
        // transient one would retry this stream first.
        engine.RaiseError(session.Snapshot.AttachGeneration, "Unable to resolve host cdn.example");

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
    public void FakeEngine_UserPrevious_AttachesPreviousPoolEntry()
    {
        // Arrange
        var current = _fixture.Create<Stream>();
        var next = _fixture.Create<Stream>();
        var (session, pool, engine, host) = CreateHost(current, next);
        host.AttachFromPool();
        engine.RaiseReady(session.Snapshot.AttachGeneration);
        host.Dispatch(MediaEngineEvent.UserNext());
        engine.RaiseReady(session.Snapshot.AttachGeneration);

        // Act
        host.Dispatch(MediaEngineEvent.UserPrevious());

        // Assert
        Assert.Equal(current.Channel, pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal(current.Url, engine.LastAttached);
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
        engine.RaiseError(session.Snapshot.AttachGeneration, "Unable to resolve host cdn.example");

        // Assert
        Assert.Null(pool.GetCurrentStream());
        Assert.Empty(pool.GetHealthyStreams());
        Assert.Single(engine.AttachLog);
        Assert.Equal(PlaybackSessionState.Failed, session.Snapshot.State);
    }

    private (PlaybackSessionController session, StreamSwitchingService pool, FakeMediaEngine engine, EngineHost host)
        CreateHost(params Stream[] streams) => CreateHost(null, streams);

    private (PlaybackSessionController session, StreamSwitchingService pool, FakeMediaEngine engine, EngineHost host)
        CreateHost(ResolveFreshPlaybackUrlAsync? resolveFresh, params Stream[] streams)
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
        var host = new EngineHost(session, pool, engine, resolveFresh);
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

    private sealed class EngineHost : IPlaybackCommandHost
    {
        private readonly PlaybackSessionController _session;
        private readonly StreamSwitchingService _pool;
        private readonly FakeMediaEngine _engine;
        private readonly PlaybackPoolCommandActions _poolActions;

        public List<string?> Failed { get; } = [];

        public EngineHost(
            PlaybackSessionController session,
            StreamSwitchingService pool,
            FakeMediaEngine engine,
            ResolveFreshPlaybackUrlAsync? resolveFresh = null)
        {
            _session = session;
            _pool = pool;
            _engine = engine;
            _poolActions = new PlaybackPoolCommandActions(
                session,
                pool,
                resolveFresh: resolveFresh,
                attachViaSession: (url, usedCached, force) =>
                {
                    session.SetHealthyStreamCount(pool.GetHealthyStreams().Count);
                    PlaybackCommandExecutor.Apply(
                        PlaybackCommand.FromEffects(session.BeginAttach(url, usedCached, force)),
                        this);
                },
                applyCommand: cmd => PlaybackCommandExecutor.Apply(cmd, this))
            {
                DelayAsync = static _ => Task.CompletedTask
            };
        }

        public void AttachFromPool()
        {
            var url = _pool.GetCurrentStream()?.ResolvedM3U8Url;
            if (string.IsNullOrWhiteSpace(url)) return;
            PlaybackCommandExecutor.Apply(
                PlaybackCommand.FromEffects(_session.BeginAttach(url, usedCachedUrl: false)),
                this);
        }

        public void Dispatch(MediaEngineEvent e)
            => PlaybackCommandExecutor.Apply(PlaybackCommand.FromEffects(_session.Handle(e)), this);

        public void BeginIndexSwitchSuppression()
        {
        }

        public void EndIndexSwitchSuppression()
        {
        }

        public void ClearCurrentResolvedUrl() => _poolActions.ClearCurrentResolvedUrl();

        public void RemoveCurrentFromPool() => _poolActions.RemoveCurrentFromPool();

        public void SyncHealthyStreamCount() => _poolActions.SyncHealthyStreamCount();

        public void ReportFailed(string? reason) => Failed.Add(reason);

        public void ReportDeclined(string? reason)
        {
        }

        public void ReportWorking()
        {
        }

        public void MarkEstablished()
        {
            // Session established flag is owned by PlaybackSessionController.Handle(Ready).
        }

        public void RaiseBuffering(bool isBuffering)
        {
        }

        public void Attach(string url, bool isRevert)
            => _engine.AttachAsync(url).GetAwaiter().GetResult();

        public void AttachCurrentAfterRemove()
            => _poolActions.AttachCurrentFromPoolAsync().GetAwaiter().GetResult();

        public void RetryFreshResolve()
            => _poolActions.RetryFreshResolveAsync().GetAwaiter().GetResult();

        public void StopEngine()
            => _engine.StopAsync().GetAwaiter().GetResult();

        public void CloseSession(string reason)
        {
        }

        public void SwitchPoolToNext()
        {
            _pool.SwitchToNextStream();
            AttachFromPool();
        }

        public void SwitchPoolToPrevious()
        {
            _poolActions.SwitchPoolToPrevious();
            AttachFromPool();
        }

        public void NotifyApplyFailed(Exception exception)
        {
        }
    }
}
