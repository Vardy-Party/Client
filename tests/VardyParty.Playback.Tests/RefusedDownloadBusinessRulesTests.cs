using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Windows 251, 13 Nov 2026: V1 rows 403'd at start, V2 chips' segments were all 403, the
/// player showed "Unsupported video type or invalid file path" and failed ~5s later.
/// </summary>
public class RefusedDownloadBusinessRulesTests
{
    [Fact]
    public void Rule_TwoRefusedSegmentsAtStart_FailTheStart_RetryFreshOnce_NotFiveOrMediaFoundation()
    {
        // Arrange - a probed (cached) link that has not shown a picture yet.
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("https://fhlsport955.example/chip/index.m3u8", usedCachedUrl: true);

        // Act
        var first = session.NotifyDownloadFailure("Segment download failed (MediaSegment, status=403)", 403);
        var second = session.NotifyDownloadFailure("Segment download failed (MediaSegment, status=403)", 403);

        // Assert - retry before switch: one fresh resolve of the same row.
        Assert.All(first, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
        Assert.Contains(second, e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
    }

    [Fact]
    public void Rule_RefusedAgainAfterTheFreshResolve_MovesToTheNextStream()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(3);
        session.BeginAttach("https://fhlsport955.example/chip/index.m3u8", usedCachedUrl: true);
        session.NotifyDownloadFailure("seg", 403);
        session.NotifyDownloadFailure("seg", 403);
        session.BeginAttach("https://fhlsport955.example/chip/fresh.m3u8");

        // Act
        session.NotifyDownloadFailure("seg", 403);
        var effects = session.NotifyDownloadFailure("seg", 403);

        // Assert
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.ReportFailed
            && e.Reason!.StartsWith("Stream refused (403)", StringComparison.Ordinal));
        Assert.Contains(effects, e => e.Kind == PlaybackEffectKind.AdvanceToNext);
        var command = PlaybackCommand.FromEffects(effects);
        Assert.Equal("Stream refused (403) - trying the next stream", command.UserFacingFailureText);
    }

    [Fact]
    public void Rule_ARefusalBetweenOtherFailures_DoesNotCountAsInARow()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8", usedCachedUrl: true);

        // Act
        var a = session.NotifyDownloadFailure("seg", 403);
        var b = session.NotifyDownloadFailure("seg", 404);
        var c = session.NotifyDownloadFailure("seg", 403);

        // Assert
        Assert.All(a.Concat(b).Concat(c), e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
    }

    [Fact]
    public void Rule_ASuccessfulSegment_ResetsTheRefusedCount()
    {
        // Arrange
        var session = new PlaybackSessionController();
        session.SetHealthyStreamCount(2);
        session.BeginAttach("http://a.m3u8", usedCachedUrl: true);
        session.NotifyDownloadFailure("seg", 403);
        session.NotifyDownloadSuccess();

        // Act
        var effects = session.NotifyDownloadFailure("seg", 403);

        // Assert
        Assert.All(effects, e => Assert.Equal(PlaybackEffectKind.None, e.Kind));
    }

    [Theory]
    [InlineData(403, 0x80190193u)]
    [InlineData(401, 0x80190191u)]
    [InlineData(404, 0x80190194u)]
    [InlineData(0, 0x80004005u)]
    public void WindowsExtendedStatus_IsAFailureHResult_NotTheBareStatus(int status, uint expected)
        => Assert.Equal(expected, PlaybackPolicy.WindowsDownloadFailureStatus(status));

    [Fact]
    public void RefusedReason_IsATokenReject_EvenWithMediaFoundationsUnsupportedText()
    {
        // Arrange - what the Windows MediaFailed handler now builds.
        var detail = PlaybackPolicy.RefusedDownloadReason(403) + ": "
            + PlaybackPolicy.FormatWindowsMediaFailedMessage(true, "Unsupported video type or invalid file path");

        // Act
        var kind = PlaybackPolicy.ClassifyPlaybackError(detail);

        // Assert
        Assert.Equal(PlaybackErrorKind.TokenOrPlaylistInvalid, kind);
    }

    [Theory]
    [InlineData("Stream refused (403): Media failed: Unsupported video type or invalid file path", true, "Stream refused (403) - trying the next stream")]
    [InlineData("Failed to start playback: Response status code does not indicate success: 403 (Forbidden).", true, "Stream refused (403) - trying the next stream")]
    [InlineData("Media failed (source not supported): Unsupported video type or invalid file path", true, "Stream format not playable - trying the next stream")]
    [InlineData("Health declined (buffering)", true, "Stream kept buffering - trying the next stream")]
    [InlineData("No video (audio only)", false, "No picture (audio only)")]
    [InlineData("connection refused", false, "Stream server unreachable")]
    [InlineData(null, false, "Stream failed")]
    public void UserFacingText_IsShortAndAccurate_NeverTheRawEngineMessage(string? reason, bool movingOn, string expected)
        => Assert.Equal(expected, PlaybackPolicy.UserFacingStreamError(reason, movingOn));

    [Fact]
    public void Executor_ShowsTheUserFacingText_AfterTheReport()
    {
        // Arrange
        var host = new RecordingHost();
        var command = new PlaybackCommand(ReportFailed: true, RemoveCurrentFromPool: true, SwitchPoolToNext: false,
            AttachCurrentAfterRemove: true, Reason: "Stream refused (403): Media failed: Unknown media error");

        // Act
        PlaybackCommandExecutor.Apply(command, host);

        // Assert
        Assert.Equal(["ReportFailed", "Show:Stream refused (403) - trying the next stream"], host.Calls.Take(2));
    }

    private sealed class RecordingHost : IPlaybackCommandHost
    {
        public List<string> Calls { get; } = [];
        public void BeginIndexSwitchSuppression() { }
        public void EndIndexSwitchSuppression() { }
        public void ClearCurrentResolvedUrl() { }
        public void RemoveCurrentFromPool() { }
        public void SyncHealthyStreamCount() { }
        public void ReportFailed(string? reason) => Calls.Add("ReportFailed");
        public void ReportDeclined(string? reason) => Calls.Add("ReportDeclined");
        public void ShowFailureToUser(string userText) => Calls.Add("Show:" + userText);
        public void ReportWorking() { }
        public void MarkEstablished() { }
        public void RaiseBuffering(bool isBuffering) { }
        public void Attach(string url, bool isRevert) { }
        public void RevertPoolIndexTo(string url) { }
        public void AttachCurrentAfterRemove() => Calls.Add("AttachCurrentAfterRemove");
        public void RetryFreshResolve() { }
        public void StopEngine() { }
        public void CloseSession(string reason) { }
        public void SwitchPoolToNext() { }
        public void SwitchPoolToPrevious() { }
        public void NotifyApplyFailed(Exception exception) { }
    }
}

public class PlaybackRequestHeadersTests
{
    [Fact]
    public void RowHeadersWin_AndUserAgentRefererOriginFillTheGaps()
    {
        // Arrange - a row whose headers carry only a Cookie (the old code then sent no Referer/UA).
        var row = new Dictionary<string, string> { ["Cookie"] = "s=1" };

        // Act
        var headers = PlaybackRequestHeaders.Resolve("https://matchli.st/watch/1", row).ToDictionary(h => h.Key, h => h.Value);

        // Assert
        Assert.Equal("s=1", headers["Cookie"]);
        Assert.Equal("https://matchli.st/watch/1", headers["Referer"]);
        Assert.Equal("https://matchli.st", headers["Origin"]);
        Assert.Equal(PlaybackRequestHeaders.BrowserUserAgent, headers["User-Agent"]);
    }

    [Fact]
    public void RowsOwnRefererAndUserAgent_AreNotOverridden()
    {
        // Arrange
        var row = new Dictionary<string, string> { ["referer"] = "https://xstream.st/", ["User-Agent"] = "UA/1" };

        // Act
        var headers = PlaybackRequestHeaders.Resolve("https://other.example/", row);

        // Assert
        Assert.Single(headers, h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headers, h => h.Key == "referer" && h.Value == "https://xstream.st/");
        Assert.Contains(headers, h => h.Key == "User-Agent" && h.Value == "UA/1");
        Assert.DoesNotContain(headers, h => h.Key == "Origin");
    }

    [Fact]
    public void NoHeadersNoReferer_SendsJustTheBrowserUserAgent()
        => Assert.Equal(["User-Agent"], PlaybackRequestHeaders.Resolve(null, null).Select(h => h.Key));
}