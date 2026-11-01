using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class PlaybackRaceBusinessRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_793_137_000);

    [Fact]
    public void Decide_ProvenVideoOnRecommended_BeatsAnEarlierPartnerUrl()
    {
        // Arrange
        var recommended = RaceLegState.Video;
        var partner = RaceLegState.Unverified;

        // Act
        var decision = PlaybackRacePolicy.Decide(recommended, partner, graceElapsed: true);

        // Assert
        Assert.Equal(RaceDecision.Recommended, decision);
    }

    [Fact]
    public void Decide_ProvenVideoPartner_BeatsUnverifiedRecommendedUrl()
    {
        // Arrange
        var recommended = RaceLegState.Unverified;
        var partner = RaceLegState.Video;

        // Act
        var decision = PlaybackRacePolicy.Decide(recommended, partner, graceElapsed: false);

        // Assert
        Assert.Equal(RaceDecision.Partner, decision);
    }

    [Fact]
    public void Decide_AudioOnly_NeverWins()
    {
        // Arrange
        var recommended = RaceLegState.AudioOnly;
        var partner = RaceLegState.AudioOnly;

        // Act
        var decision = PlaybackRacePolicy.Decide(recommended, partner, graceElapsed: true);

        // Assert
        Assert.Equal(RaceDecision.NoWinner, decision);
    }

    [Fact]
    public void Decide_EmptyRecommended_PlaysReadyRowWithoutWaiting()
    {
        // Arrange
        var recommended = RaceLegState.Empty;
        var partner = RaceLegState.Pending;

        // Act
        var decision = PlaybackRacePolicy.Decide(
            recommended, partner, graceElapsed: false, otherRowCanPlayNow: true);

        // Assert
        Assert.Equal(RaceDecision.PlayReadyRow, decision);
    }

    [Fact]
    public void Decide_PendingRecommended_GivesGraceBeforePartnerUrl()
    {
        // Arrange
        var recommended = RaceLegState.Pending;
        var partner = RaceLegState.Unverified;

        // Act
        var waiting = PlaybackRacePolicy.Decide(recommended, partner, graceElapsed: false);
        var elapsed = PlaybackRacePolicy.Decide(recommended, partner, graceElapsed: true);

        // Assert
        Assert.Equal(RaceDecision.Wait, waiting);
        Assert.Equal(RaceDecision.Partner, elapsed);
    }

    [Fact]
    public void PickPartner_SkipsRowThatCanPlayFromCache()
    {
        // Arrange
        var recommended = Row("north", url: null, oneShot: true);
        var cached = Row("south", url: "https://cdn.example.com/live/south.m3u8", oneShot: false);
        var healthy = new List<EnrichedStream> { recommended, cached };

        // Act
        var partner = PlaybackRacePolicy.PickPartner(healthy, recommended, Now);

        // Assert
        Assert.Null(partner);
    }

    [Fact]
    public async Task Race_RecommendedVideo_WinsEvenWhenPartnerReturnedFirst()
    {
        // Arrange
        var recommended = Row("north", url: null, oneShot: true);
        var partner = Row("south", url: null, oneShot: true);
        var partnerHit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var race = await PlaybackUrlRacer.RaceAsync(
            recommended,
            partner,
            (row, _) =>
            {
                if (ReferenceEquals(row, partner))
                    partnerHit.TrySetResult();
                return WaitForPartner(row, recommended, partnerHit.Task);
            },
            (payload, _) => Task.FromResult(
                payload.Url.Contains("north", StringComparison.Ordinal)
                    ? RaceLegState.Video
                    : RaceLegState.Unverified),
            onFinished: null,
            otherRowCanPlayNow: null,
            CancellationToken.None,
            grace: TimeSpan.FromSeconds(5));

        // Assert
        Assert.Same(recommended, race.Winner);
        Assert.Equal("https://cdn.example.com/north.m3u8", race.WinnerValue?.Url);
    }

    [Fact]
    public async Task Race_EmptyRecommended_DoesNotWaitForPartnerWhenAnotherRowCanPlay()
    {
        // Arrange
        var recommended = Row("north", url: null, oneShot: true);
        var partner = Row("south", url: null, oneShot: true);
        var started = DateTime.UtcNow;

        // Act
        var race = await PlaybackUrlRacer.RaceAsync(
            recommended,
            partner,
            (row, _) => ReferenceEquals(row, partner)
                ? Never()
                : Task.FromResult<Payload?>(null),
            (_, _) => Task.FromResult(RaceLegState.Video),
            onFinished: null,
            otherRowCanPlayNow: () => true,
            CancellationToken.None,
            grace: TimeSpan.FromSeconds(30));

        // Assert
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        Assert.True(race.PlayReadyRow);
        Assert.Null(race.Winner);
        Assert.Equal(RaceLegState.Empty, race.Recommended?.State);
    }

    [Fact]
    public async Task Race_AudioOnlyRecommended_LosesToPartnerVideo()
    {
        // Arrange
        var recommended = Row("north", url: null, oneShot: true);
        var partner = Row("south", url: null, oneShot: true);

        // Act
        var race = await PlaybackUrlRacer.RaceAsync(
            recommended,
            partner,
            (row, _) => Task.FromResult<Payload?>(new Payload("https://cdn.example.com/" + row.Stream!.Channel + ".m3u8")),
            (payload, _) => Task.FromResult(
                payload.Url.Contains("north", StringComparison.Ordinal)
                    ? RaceLegState.AudioOnly
                    : RaceLegState.Video),
            onFinished: null,
            otherRowCanPlayNow: null,
            CancellationToken.None);

        // Assert
        Assert.Same(partner, race.Winner);
        Assert.Equal(RaceLegState.AudioOnly, race.Recommended?.State);
        Assert.Contains(race.StreamVerdicts, leg => ReferenceEquals(leg.Row, recommended));
    }

    private static async Task<Payload?> WaitForPartner(EnrichedStream row, EnrichedStream recommended, Task partnerHit)
    {
        if (!ReferenceEquals(row, recommended))
            return new Payload("https://cdn.example.com/south.m3u8");
        await partnerHit.ConfigureAwait(false);
        return new Payload("https://cdn.example.com/north.m3u8");
    }

    private static Task<Payload?> Never()
    {
        var pending = new TaskCompletionSource<Payload?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return pending.Task;
    }

    private static EnrichedStream Row(string channel, string? url, bool oneShot)
        => new()
        {
            Stream = new Stream { Url = "https://streams.example.com/" + channel, Channel = channel },
            Status = StreamResolutionStatus.Healthy,
            ResolvedM3U8Url = url,
            RequestHeaders = oneShot ? new Dictionary<string, string> { ["Referer"] = "https://player.example.com/" } : null,
        };

    private sealed record Payload(string Url);
}
