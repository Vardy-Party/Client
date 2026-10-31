using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Healthy pool dedup business rules (docs/STREAM_PLAYBACK_RULES.md, "Healthy pool
/// dedup"). Every client (Android, Windows, Linux, Apple) shows the
/// <see cref="StreamSwitchingService"/> pool, so these rules hold on every platform:
/// one visible entry per m3u8 identity (<see cref="StreamUrlNormalizer.M3u8IdentityKey"/>),
/// first healthy wins unless a non-playing entry is outranked by reputation, merged
/// sources are hidden fallbacks promoted in place on failure, and different channels
/// never merge. URLs are the real ones from the 27 Oct 2026 Fleetwood v Arsenal session.
/// </summary>
public class HealthyPoolDedupBusinessRulesTests
{
    private const string Premium38A =
        "https://edge.cowedd4855ws.sbs/premium38/index.m3u8?t=1793137079.2f8f0bfaab9d30b80ba4ec68055266c1&_=1793136956751";
    private const string Premium38B =
        "https://edge.cowedd4855ws.sbs/premium38/index.m3u8?t=1793137099.897440ff42dc2a63570297f7e130584c&_=1793136976705";
    private const string Premium38C =
        "https://edge.cowedd4855ws.sbs/premium38/index.m3u8?t=1793137110.1c1d6f4866eaeea5d02b1e9d941405d1&_=1793136988143";
    private const string Premium35 =
        "https://edge.cowedd4855ws.sbs/premium35/index.m3u8?t=1793137120.0a1b2c3d4e5f60718293a4b5c6d7e8f9&_=1793136999999";
    private const string UzhiA =
        "https://tims-nl-0001.uzhi.ru/main/secure/c9db667f6412f888e07a9f1a3c3d18f6e00c6a59d09a253bbd2ae780fc21ff13/1793145999/skysportsfootball-uk.m3u8";
    private const string UzhiB =
        "https://tims-nl-0001.uzhi.ru/main/secure/08b298ba5cd8832d6c55ff57e4bece14a47d87774eb6ced6ac96b9492ac119bc/1793146015/skysportsfootball-uk.m3u8";
    private const string UzhiC =
        "https://tims-nl-0001.uzhi.ru/main/secure/3eeb169fad82fa1578288d1e8cff40c22a22a8d57f0c9603386c426a4716bcab/1793146067/skysportsfootball-uk.m3u8";

    private static EnrichedStream Healthy(
        string channel,
        string m3u8,
        StreamReputation reputation = StreamReputation.None) => new()
        {
            Stream = new StreamModel
            {
                Url = $"https://{channel.ToLowerInvariant()}.example/fleetwood-town-vs-arsenal",
                Channel = channel,
                Reputation = reputation,
            },
            ResolvedM3U8Url = m3u8,
            Status = StreamResolutionStatus.Healthy,
        };

    private static StreamSwitchingService NewPool()
    {
        var pool = new StreamSwitchingService();
        pool.Initialize("League Cup", "Fleetwood Town", "Arsenal");
        return pool;
    }

    private static List<string> Visible(StreamSwitchingService pool) =>
        pool.GetHealthyStreams().Select(s => s.Stream.Channel).ToList();

    [Fact]
    public void Rule1_SameCowEddChannelFromThreeSites_ShowsOnce_DifferentChannelStaysSeparate()
    {
        var pool = NewPool();

        pool.AddHealthyStream(Healthy("Liveverse", Premium38A));
        pool.AddHealthyStream(Healthy("1ststreams", Premium38B));
        pool.AddHealthyStream(Healthy("Pitchstream", Premium38C));
        pool.AddHealthyStream(Healthy("Totalsportek", Premium35));

        Assert.Equal(["Liveverse", "Totalsportek"], Visible(pool));
    }

    [Fact]
    public void Rule1_SameUzhiChannelWithPerPlayPathTokens_ShowsOnce()
    {
        var pool = NewPool();

        pool.AddHealthyStream(Healthy("soccermlbstream", UzhiA));
        pool.AddHealthyStream(Healthy("All-Sports-HD", UzhiB));
        pool.AddHealthyStream(Healthy("Vivoentv", UzhiC));

        Assert.Equal(["soccermlbstream"], Visible(pool));
    }

    [Fact]
    public void Rule2_FirstHealthyWins_WhenReputationIsNotHigher()
    {
        var pool = NewPool();

        pool.AddHealthyStream(Healthy("Liveverse", Premium38A, StreamReputation.Good));
        pool.AddHealthyStream(Healthy("1ststreams", Premium38B, StreamReputation.Good));

        Assert.Equal(["Liveverse"], Visible(pool));
    }

    [Fact]
    public void Rule2_HigherReputationReplacesVisible_OnlyWhenItIsNotPlaying()
    {
        var pool = NewPool();
        pool.AddHealthyStream(Healthy("Totalsportek", Premium35));
        pool.AddHealthyStream(Healthy("Liveverse", Premium38A, StreamReputation.Poor));

        pool.AddHealthyStream(Healthy("1ststreams", Premium38B, StreamReputation.VeryGood));

        Assert.Equal(["Totalsportek", "1ststreams"], Visible(pool));
        Assert.Equal(1, pool.GetCurrentStreamIndex());
        Assert.Equal("Liveverse", Assert.Single(pool.GetMergedFallbacks(pool.GetHealthyStreams()[1])).Stream.Channel);
    }

    [Fact]
    public void Rule2_PlayingEntryIsNeverSwappedUnderTheViewer()
    {
        var pool = NewPool();
        pool.AddHealthyStream(Healthy("Liveverse", Premium38A, StreamReputation.Poor));

        pool.AddHealthyStream(Healthy("1ststreams", Premium38B, StreamReputation.VeryGood));

        Assert.Equal(["Liveverse"], Visible(pool));
        Assert.Equal("Liveverse", pool.GetCurrentStream()!.Stream.Channel);
    }

    [Fact]
    public void Rule3_FailedEntryPromotesMergedFallbackInPlace_ThenShrinks()
    {
        var pool = NewPool();
        pool.AddHealthyStream(Healthy("Liveverse", Premium38A));
        pool.AddHealthyStream(Healthy("Totalsportek", Premium35));
        pool.AddHealthyStream(Healthy("1ststreams", Premium38B));
        var emittedIndexes = new List<int>();
        using var sub = pool.CurrentStreamIndexChanged.Subscribe(emittedIndexes.Add);
        emittedIndexes.Clear();

        Assert.True(pool.RemoveCurrentStream());

        Assert.Equal(["1ststreams", "Totalsportek"], Visible(pool));
        Assert.Equal("1ststreams", pool.GetCurrentStream()!.Stream.Channel);
        Assert.Equal([0], emittedIndexes);

        Assert.True(pool.RemoveCurrentStream());

        Assert.Equal(["Totalsportek"], Visible(pool));
    }

    [Fact]
    public void Rule3_FallbackPromotionPrefersHigherReputation()
    {
        var pool = NewPool();
        pool.AddHealthyStream(Healthy("soccermlbstream", UzhiA));
        pool.AddHealthyStream(Healthy("All-Sports-HD", UzhiB, StreamReputation.Ok));
        pool.AddHealthyStream(Healthy("Vivoentv", UzhiC, StreamReputation.VeryGood));

        pool.RemoveCurrentStream();

        Assert.Equal(["Vivoentv"], Visible(pool));
    }

    [Fact]
    public void Rule4_IdentityIsCapturedOnAdd_SoLaterUrlMutationDoesNotLetDuplicatesIn()
    {
        var pool = NewPool();
        var playing = Healthy("Liveverse", Premium38A);
        pool.AddHealthyStream(playing);

        // Fresh one-shot /play rewrites the URL; a failure clears it.
        playing.ResolvedM3U8Url = Premium38C;
        pool.AddHealthyStream(Healthy("1ststreams", Premium38B));
        playing.ResolvedM3U8Url = null;
        pool.AddHealthyStream(Healthy("Pitchstream", Premium38C));

        Assert.Equal(["Liveverse"], Visible(pool));
    }

    [Fact]
    public void Rule5_StreamsWithoutResolvedUrl_AreNeverCollapsedTogether()
    {
        var pool = NewPool();

        pool.AddHealthyStream(Healthy("A", string.Empty));
        pool.AddHealthyStream(Healthy("B", string.Empty));

        Assert.Equal(["A", "B"], Visible(pool));
    }

    [Fact]
    public void Rule6_ChipForMergedSource_SelectsItsVisibleEntry()
    {
        var pool = NewPool();
        pool.AddHealthyStream(Healthy("Totalsportek", Premium35));
        pool.AddHealthyStream(Healthy("Liveverse", Premium38A));
        var merged = Healthy("1ststreams", Premium38B);
        pool.AddHealthyStream(merged);

        Assert.True(pool.SwitchToMatchingStream(merged.Stream.Url, null));

        Assert.Equal("Liveverse", pool.GetCurrentStream()!.Stream.Channel);
    }

    [Fact]
    public void Rule6_ChipNamingMergedSource_SelectsItsEntryWithoutReordering()
    {
        // Stable channel numbering: a recommendation/chip naming a merged source
        // jumps to its visible entry; the numbered order never changes.
        var pool = NewPool();
        pool.AddHealthyStream(Healthy("Totalsportek", Premium35));
        pool.AddHealthyStream(Healthy("Liveverse", Premium38A));
        var merged = Healthy("1ststreams", Premium38B);
        pool.AddHealthyStream(merged);

        Assert.True(pool.SwitchToMatchingStream(merged.Stream.Url, null));

        Assert.Equal(["Totalsportek", "Liveverse"], Visible(pool));
        Assert.Equal(2, pool.GetCurrentStreamIndex());
    }

    [Fact]
    public void Rule7_InitializeAndCleanup_DropMergedFallbacks()
    {
        var pool = NewPool();
        pool.AddHealthyStream(Healthy("Liveverse", Premium38A));
        pool.AddHealthyStream(Healthy("1ststreams", Premium38B));

        pool.Initialize("League Cup", "Fleetwood Town", "Arsenal");
        pool.AddHealthyStream(Healthy("Pitchstream", Premium38C));
        pool.RemoveCurrentStream();

        Assert.Empty(pool.GetHealthyStreams());
    }
}