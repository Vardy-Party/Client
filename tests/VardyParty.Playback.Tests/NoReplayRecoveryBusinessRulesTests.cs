using System.Linq;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// No recovery path may re-show already-watched video. A Windows mid-play
/// network failure is never reattached to the same URL; its kind comes from the
/// last download status so the shared session switches (option 2 returns later).
/// </summary>
public class NoReplayRecoveryBusinessRulesTests
{
    [Theory]
    [InlineData(0, PlaybackErrorKind.CdnUnreachable)]
    [InlineData(503, PlaybackErrorKind.CdnUnreachable)]
    [InlineData(404, PlaybackErrorKind.ConfirmedDead)]
    [InlineData(410, PlaybackErrorKind.ConfirmedDead)]
    [InlineData(401, PlaybackErrorKind.TokenOrPlaylistInvalid)]
    [InlineData(403, PlaybackErrorKind.TokenOrPlaylistInvalid)]
    public void WindowsNetworkFailure_KindFollowsTheLastDownloadStatus(int lastStatus, PlaybackErrorKind expected)
    {
        // Arrange
        const string detail = "Media failed: The download of the media resource failed.";

        // Act
        var message = PlaybackPolicy.WindowsMidPlayNetworkFailureMessage(lastStatus, detail);

        // Assert
        Assert.Equal(expected, PlaybackPolicy.ClassifyPlaybackError(message));
    }

    [Fact]
    public void WindowsNetworkFailure_MidPlay_SwitchesWithoutSameUrlReattach()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("https://cdn.example.com/channel-north.m3u8");
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        var message = PlaybackPolicy.WindowsMidPlayNetworkFailureMessage(0, "Media failed: network error");

        // Act
        var effects = session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, message));

        // Assert
        Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.Attach or PlaybackEffectKind.RetryFreshResolve);
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        Assert.True(SwitchBackCommands.IsCdnUnreachableMoveOn(
            effects.First(e => e.Kind == PlaybackEffectKind.AdvanceToNext).Reason));
    }
}
