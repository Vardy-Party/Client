using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Living recommendation business rules: empty-rec discovery spread, chip hoist,
/// preferred-next, and wraparound refresh triggers.
/// </summary>
public class StreamRecommendationBusinessRulesTests
{
    [Fact]
    public void EmptyRecDiscovery_DifferentSessionSalts_SpreadFirstFbTry()
    {
        var streams = new[]
        {
            Fb("https://streams.example.com/a", "A"),
            Fb("https://streams.example.com/b", "B"),
            Fb("https://streams.example.com/c", "C"),
            Mp("https://mp.example.com/page", "MP")
        };

        var order0 = StreamRecommendationPolicy.SpreadDiscoveryOrder(
            streams.Length, i => streams[i], sessionSalt: 0);
        var order1 = StreamRecommendationPolicy.SpreadDiscoveryOrder(
            streams.Length, i => streams[i], sessionSalt: 1);

        Assert.Equal("mp", streams[order0[0]].ResolveCatalogSource());
        Assert.Equal(3, order0.Skip(1).Count(i => streams[i].ResolveCatalogSource() == "fb"));
        Assert.NotEqual(
            streams[order0[1]].Channel,
            streams[order1[1]].Channel);
    }

    [Fact]
    public void ApplyRecommendedChips_SynthesizesMpChip_AndDropsBarePage()
    {
        var page = "https://www.example-mp.invalid/football/match.html";
        var catalog = new List<StreamModel>
        {
            Fb("https://streams.example.com/alpha", "Alpha"),
            new StreamModel
            {
                Url = page,
                Source = "mp",
                ResolutionStrategy = "v2",
                Channel = "MP"
            }
        };
        var recommendations = new RecommendationResponse
        {
            HasData = true,
            Confidence = RecommendationConfidence.High,
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Recommended =
            [
                new RecommendationItem
                {
                    Url = page,
                    StreamName = "SKA",
                    Confidence = RecommendationConfidence.High
                }
            ]
        };

        var applied = StreamRecommendationPolicy.ApplyRecommendedChips(catalog, recommendations);

        Assert.Contains(applied, s => s.PlayerStream == "SKA");
        Assert.DoesNotContain(applied, s =>
            StreamHealthIdentity.NormalizeStreamUrl(s.Url)
                == StreamHealthIdentity.NormalizeStreamUrl(page)
            && string.IsNullOrWhiteSpace(StreamHealthIdentity.GetStreamName(s)));
    }

    [Fact]
    public void PickPreferredNext_ChoosesHighestConfidenceNonCurrent()
    {
        var page = "https://www.example-mp.invalid/football/match.html";
        var current = Healthy(page, "Canal FR");
        var better = Healthy(page, "SKA");
        var fb = Healthy("https://streams.example.com/fb", null);
        var recommendations = new RecommendationResponse
        {
            HasData = true,
            Confidence = RecommendationConfidence.High,
            GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Recommended =
            [
                new RecommendationItem
                {
                    Url = page,
                    StreamName = "SKA",
                    Confidence = RecommendationConfidence.High
                },
                new RecommendationItem
                {
                    Url = page,
                    StreamName = "Canal FR",
                    Confidence = RecommendationConfidence.Low
                },
                new RecommendationItem
                {
                    Url = fb.Stream.Url,
                    Confidence = RecommendationConfidence.Medium
                }
            ]
        };

        var preferred = StreamRecommendationPolicy.PickPreferredNext(
            recommendations,
            [current, better, fb],
            current);

        Assert.Same(better, preferred);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ShouldPreferRecommendedPeerOnNext_OnlyWhenWrapping(
        bool wouldWrapAround,
        bool expected) =>
        Assert.Equal(
            expected,
            StreamRecommendationPolicy.ShouldPreferRecommendedPeerOnNext(wouldWrapAround));

    [Fact]
    public void MidCycleNext_MustNotTrapInRecommendedSubset()
    {
        // Regression: UI showed 6 healthy streams but Next bounced between the
        // 2 recommended chips because preferred-next ran every press.
        Assert.False(StreamRecommendationPolicy.ShouldPreferRecommendedPeerOnNext(wouldWrapAround: false));
    }

    [Fact]
    public void IsStale_MissingOrOldGeneratedAt_IsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(StreamRecommendationPolicy.IsStale(null, now, TimeSpan.FromSeconds(45)));
        Assert.True(StreamRecommendationPolicy.IsStale(
            new RecommendationResponse { HasData = true },
            now,
            TimeSpan.FromSeconds(45)));
        Assert.True(StreamRecommendationPolicy.IsStale(
            new RecommendationResponse
            {
                GeneratedAt = now.AddMinutes(-2).ToUnixTimeMilliseconds()
            },
            now,
            TimeSpan.FromSeconds(45)));
        Assert.False(StreamRecommendationPolicy.IsStale(
            new RecommendationResponse
            {
                GeneratedAt = now.AddSeconds(-10).ToUnixTimeMilliseconds()
            },
            now,
            TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public void WouldWrapAround_LastIndex_IsTrue()
    {
        Assert.True(StreamRecommendationPolicy.WouldWrapAround(currentIndex: 2, healthyCount: 3));
        Assert.False(StreamRecommendationPolicy.WouldWrapAround(currentIndex: 0, healthyCount: 3));
        Assert.False(StreamRecommendationPolicy.WouldWrapAround(currentIndex: -1, healthyCount: 3));
    }

    private static StreamModel Fb(string url, string channel) =>
        new()
        {
            Url = url,
            Channel = channel,
            Source = "fb"
        };

    private static StreamModel Mp(string url, string channel) =>
        new()
        {
            Url = url,
            Channel = channel,
            Source = "mp",
            ResolutionStrategy = "v2"
        };

    private static EnrichedStream Healthy(string url, string? chip) =>
        new()
        {
            Stream = new StreamModel
            {
                Url = url,
                Channel = chip ?? "FB",
                PlayerStream = chip ?? string.Empty,
                Source = chip == null ? "fb" : "mp",
                ResolutionStrategy = chip == null ? string.Empty : "v2"
            },
            Status = StreamResolutionStatus.Healthy,
            ResolvedM3U8Url = url + "/index.m3u8"
        };
}
