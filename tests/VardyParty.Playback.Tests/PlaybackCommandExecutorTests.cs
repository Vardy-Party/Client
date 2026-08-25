using System;
using System.Collections.Generic;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

public class PlaybackCommandExecutorTests
{
    [Fact]
    public void Apply_NoOp_DoesNotTouchHost()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand();

        // Act
        var closed = PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.False(closed);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Apply_ClearRemoveAndReports_UsesPaidHostFlagOrder()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var reason = "oak-lane decoder";
        var cmd = new PlaybackCommand(
            ClearResolvedUrl: true,
            RemoveCurrentFromPool: true,
            ReportFailed: true,
            ReportDeclined: true,
            Reason: reason);

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Equal(
            new[]
            {
                nameof(IPlaybackCommandHost.BeginIndexSwitchSuppression),
                nameof(IPlaybackCommandHost.ClearCurrentResolvedUrl),
                nameof(IPlaybackCommandHost.RemoveCurrentFromPool),
                nameof(IPlaybackCommandHost.SyncHealthyStreamCount),
                nameof(IPlaybackCommandHost.ReportFailed),
                nameof(IPlaybackCommandHost.ReportDeclined),
                nameof(IPlaybackCommandHost.EndIndexSwitchSuppression)
            },
            host.Calls);
        Assert.Equal(reason, host.LastFailedReason);
        Assert.Equal(reason, host.LastDeclinedReason);
    }

    [Fact]
    public void Apply_LinuxPreviouslySkippedFlags_AreInvoked()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(
            ReportFailed: true,
            ReportDeclined: true,
            RaiseBuffering: true,
            IsBuffering: true,
            RetryFreshResolve: true,
            Reason: "northgate health");

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Contains(nameof(IPlaybackCommandHost.ReportFailed), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.ReportDeclined), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.RaiseBuffering), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.RetryFreshResolve), host.Calls);
        Assert.True(host.LastBuffering);
    }

    [Fact]
    public void Apply_AttachUrl_TakesPrecedenceOverAttachCurrentAfterRemove()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var url = "http://oak-lane.m3u8";
        var cmd = new PlaybackCommand(
            AttachUrl: url,
            AttachIsRevert: true,
            AttachCurrentAfterRemove: true);

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Contains(nameof(IPlaybackCommandHost.Attach), host.Calls);
        Assert.DoesNotContain(nameof(IPlaybackCommandHost.AttachCurrentAfterRemove), host.Calls);
        Assert.Equal(url, host.LastAttachUrl);
        Assert.True(host.LastAttachIsRevert);
    }

    [Fact]
    public void Apply_AttachCurrentAfterRemove_WhenNoAttachUrl()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(AttachCurrentAfterRemove: true);

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Contains(nameof(IPlaybackCommandHost.AttachCurrentAfterRemove), host.Calls);
        Assert.Null(host.LastAttachUrl);
    }

    [Fact]
    public void Apply_StopAndSwitchPrevious_UnsuppressesBeforeSwitch()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(Stop: true, SwitchPoolToPrevious: true);

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Equal(
            new[]
            {
                nameof(IPlaybackCommandHost.BeginIndexSwitchSuppression),
                nameof(IPlaybackCommandHost.SyncHealthyStreamCount),
                nameof(IPlaybackCommandHost.StopEngine),
                nameof(IPlaybackCommandHost.EndIndexSwitchSuppression),
                nameof(IPlaybackCommandHost.SwitchPoolToPrevious)
            },
            host.Calls);
    }

    [Fact]
    public void Apply_CloseSession_SkipsPoolSwitch()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(
            CloseSession: true,
            CloseReason: "oak-lane last stream",
            SwitchPoolToNext: true);

        // Act
        var closed = PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.True(closed);
        Assert.Equal("oak-lane last stream", host.LastCloseReason);
        Assert.DoesNotContain(nameof(IPlaybackCommandHost.SwitchPoolToNext), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.EndIndexSwitchSuppression), host.Calls);
    }

    [Fact]
    public void Apply_CloseSessionWithoutReason_UsesPlaybackFailed()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(CloseSession: true);

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Equal("Playback failed", host.LastCloseReason);
    }

    [Fact]
    public void Apply_ReportDeclinedWithoutReason_UsesHealthDeclined()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(ReportDeclined: true);

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Equal("Health declined", host.LastDeclinedReason);
    }

    [Fact]
    public void Apply_SwitchPoolToNext_RunsAfterUnsuppress()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(SwitchPoolToNext: true);

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Equal(
            new[]
            {
                nameof(IPlaybackCommandHost.BeginIndexSwitchSuppression),
                nameof(IPlaybackCommandHost.SyncHealthyStreamCount),
                nameof(IPlaybackCommandHost.EndIndexSwitchSuppression),
                nameof(IPlaybackCommandHost.SwitchPoolToNext)
            },
            host.Calls);
    }

    [Fact]
    public void Apply_WhenAttachThrows_StillSwitchesPool()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost
        {
            AttachThrows = new InvalidOperationException("oak-lane attach failed")
        };
        var cmd = new PlaybackCommand(
            AttachUrl: "http://oak-lane.m3u8",
            SwitchPoolToNext: true);

        // Act
        var closed = PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.False(closed);
        Assert.Contains(nameof(IPlaybackCommandHost.NotifyApplyFailed), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.SwitchPoolToNext), host.Calls);
        var unsuppress = host.Calls.IndexOf(nameof(IPlaybackCommandHost.EndIndexSwitchSuppression));
        var switchNext = host.Calls.IndexOf(nameof(IPlaybackCommandHost.SwitchPoolToNext));
        Assert.True(unsuppress >= 0 && switchNext > unsuppress);
    }

    [Fact]
    public void Apply_EstablishedHardFailCommand_MatchesPaidHostFlags()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("http://northgate.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "decoder")));

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.True(cmd.RemoveCurrentFromPool);
        Assert.True(cmd.AttachCurrentAfterRemove);
        Assert.True(cmd.ReportFailed);
        Assert.False(cmd.SwitchPoolToNext);
        Assert.Contains(nameof(IPlaybackCommandHost.RemoveCurrentFromPool), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.AttachCurrentAfterRemove), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.ReportFailed), host.Calls);
        Assert.DoesNotContain(nameof(IPlaybackCommandHost.SwitchPoolToNext), host.Calls);
    }

    [Fact]
    public void Apply_CachedStartFailCommand_RetriesFreshResolve()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://cached.m3u8", usedCachedUrl: true);
        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, "403")));

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.True(cmd.RetryFreshResolve);
        Assert.True(cmd.ClearResolvedUrl);
        Assert.Contains(nameof(IPlaybackCommandHost.ClearCurrentResolvedUrl), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.RetryFreshResolve), host.Calls);
    }

    [Fact]
    public void Apply_ReadyEffects_ReportsWorkingAndMarkEstablished()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var session = new PlaybackSessionController();
        session.BeginAttach("http://oak-lane.m3u8");
        var cmd = PlaybackCommand.FromEffects(
            session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration)));

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.True(cmd.ReportWorking);
        Assert.True(cmd.MarkEstablished);
        Assert.Contains(nameof(IPlaybackCommandHost.MarkEstablished), host.Calls);
        Assert.Contains(nameof(IPlaybackCommandHost.ReportWorking), host.Calls);
        var mark = host.Calls.IndexOf(nameof(IPlaybackCommandHost.MarkEstablished));
        var working = host.Calls.IndexOf(nameof(IPlaybackCommandHost.ReportWorking));
        Assert.True(mark < working);
    }

    [Fact]
    public void Apply_EveryHostFlag_UsesPaidHostOrder()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(
            AttachUrl: "http://oak-lane.m3u8",
            ClearResolvedUrl: true,
            RemoveCurrentFromPool: true,
            SwitchPoolToNext: true,
            RetryFreshResolve: true,
            Stop: true,
            RaiseBuffering: true,
            IsBuffering: false,
            ReportFailed: true,
            ReportDeclined: true,
            ReportWorking: true,
            MarkEstablished: true,
            Reason: "oak-lane decoder");

        // Act
        var closed = PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.False(closed);
        Assert.False(host.LastBuffering);
        Assert.Equal(
            new[]
            {
                nameof(IPlaybackCommandHost.BeginIndexSwitchSuppression),
                nameof(IPlaybackCommandHost.ClearCurrentResolvedUrl),
                nameof(IPlaybackCommandHost.RemoveCurrentFromPool),
                nameof(IPlaybackCommandHost.SyncHealthyStreamCount),
                nameof(IPlaybackCommandHost.ReportFailed),
                nameof(IPlaybackCommandHost.ReportDeclined),
                nameof(IPlaybackCommandHost.MarkEstablished),
                nameof(IPlaybackCommandHost.ReportWorking),
                nameof(IPlaybackCommandHost.RaiseBuffering),
                nameof(IPlaybackCommandHost.Attach),
                nameof(IPlaybackCommandHost.RetryFreshResolve),
                nameof(IPlaybackCommandHost.StopEngine),
                nameof(IPlaybackCommandHost.EndIndexSwitchSuppression),
                nameof(IPlaybackCommandHost.SwitchPoolToNext)
            },
            host.Calls);
    }

    [Fact]
    public void Apply_CloseSessionWithoutCloseReason_FallsBackToReason()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(CloseSession: true, Reason: "oak-lane last stream");

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Equal("oak-lane last stream", host.LastCloseReason);
    }

    [Fact]
    public void Apply_SwitchNextWinsOverPrevious()
    {
        // Arrange
        var host = new RecordingPlaybackCommandHost();
        var cmd = new PlaybackCommand(SwitchPoolToNext: true, SwitchPoolToPrevious: true);

        // Act
        PlaybackCommandExecutor.Apply(cmd, host);

        // Assert
        Assert.Contains(nameof(IPlaybackCommandHost.SwitchPoolToNext), host.Calls);
        Assert.DoesNotContain(nameof(IPlaybackCommandHost.SwitchPoolToPrevious), host.Calls);
    }

    private sealed class RecordingPlaybackCommandHost : IPlaybackCommandHost
    {
        public List<string> Calls { get; } = new();
        public string? LastFailedReason { get; private set; }
        public string? LastDeclinedReason { get; private set; }
        public bool LastBuffering { get; private set; }
        public string? LastAttachUrl { get; private set; }
        public bool LastAttachIsRevert { get; private set; }
        public string? LastCloseReason { get; private set; }
        public Exception? AttachThrows { get; init; }

        public void BeginIndexSwitchSuppression() => Calls.Add(nameof(BeginIndexSwitchSuppression));
        public void EndIndexSwitchSuppression() => Calls.Add(nameof(EndIndexSwitchSuppression));
        public void ClearCurrentResolvedUrl() => Calls.Add(nameof(ClearCurrentResolvedUrl));
        public void RemoveCurrentFromPool() => Calls.Add(nameof(RemoveCurrentFromPool));
        public void SyncHealthyStreamCount() => Calls.Add(nameof(SyncHealthyStreamCount));

        public void ReportFailed(string? reason)
        {
            LastFailedReason = reason;
            Calls.Add(nameof(ReportFailed));
        }

        public void ReportDeclined(string? reason)
        {
            LastDeclinedReason = reason;
            Calls.Add(nameof(ReportDeclined));
        }

        public void ReportWorking() => Calls.Add(nameof(ReportWorking));

        public void MarkEstablished() => Calls.Add(nameof(MarkEstablished));

        public void RaiseBuffering(bool isBuffering)
        {
            LastBuffering = isBuffering;
            Calls.Add(nameof(RaiseBuffering));
        }

        public void Attach(string url, bool isRevert)
        {
            LastAttachUrl = url;
            LastAttachIsRevert = isRevert;
            Calls.Add(nameof(Attach));
            if (AttachThrows != null)
                throw AttachThrows;
        }

        public void AttachCurrentAfterRemove() => Calls.Add(nameof(AttachCurrentAfterRemove));
        public void RetryFreshResolve() => Calls.Add(nameof(RetryFreshResolve));
        public void StopEngine() => Calls.Add(nameof(StopEngine));

        public void CloseSession(string reason)
        {
            LastCloseReason = reason;
            Calls.Add(nameof(CloseSession));
        }

        public void SwitchPoolToNext() => Calls.Add(nameof(SwitchPoolToNext));
        public void SwitchPoolToPrevious() => Calls.Add(nameof(SwitchPoolToPrevious));

        public void NotifyApplyFailed(Exception exception)
        {
            Calls.Add(nameof(NotifyApplyFailed));
        }
    }
}
