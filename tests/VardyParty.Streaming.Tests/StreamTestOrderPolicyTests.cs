using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests;

public class StreamTestOrderPolicyTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void ShouldPreferRecommendations_NonEmptyList_IsTrueEvenWhenConfidenceIsLow()
    {
        // Arrange
        var recommendations = new RecommendationResponse
        {
            Confidence = RecommendationConfidence.Low,
            HasData = true,
            Recommended =
            [
                new RecommendationItem { Url = "https://streams.example.com/northgate" }
            ]
        };

        // Act
        var prefer = StreamTestOrderPolicy.ShouldPreferRecommendations(recommendations);

        // Assert
        Assert.True(prefer);
    }

    [Fact]
    public void ShouldPreferRecommendations_EmptyOrMissingList_IsFalse()
    {
        // Arrange
        var empty = new RecommendationResponse { Confidence = RecommendationConfidence.High, HasData = true };
        RecommendationResponse? missing = null;

        // Act
        var preferEmpty = StreamTestOrderPolicy.ShouldPreferRecommendations(empty);
        var preferMissing = StreamTestOrderPolicy.ShouldPreferRecommendations(missing);

        // Assert
        Assert.False(preferEmpty);
        Assert.False(preferMissing);
    }

    [Fact]
    public void Build_LowConfidenceRecommendedStream_IsTriedBeforeCatalogNeighbors()
    {
        // Arrange — dashboard badges recommended in catalog order; playback must not.
        var streams = new[]
        {
            Fb("https://streams.example.com/weak-alpha", "Channel Alpha"),
            Fb("https://streams.example.com/weak-bravo", "Channel Bravo"),
            Fb("https://streams.example.com/northgate", "Channel North")
        };
        var recommendations = new RecommendationResponse
        {
            Confidence = RecommendationConfidence.Low,
            HasData = true,
            Recommended =
            [
                new RecommendationItem { Url = "https://streams.example.com/northgate" }
            ]
        };

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations,
            streams.Length,
            (url, _) => Array.FindIndex(streams, s => s.Url == url),
            index => streams[index]);

        // Assert
        Assert.Equal([2, 0, 1], order);
    }

    [Fact]
    public void Build_RecommendedMpStream_StaysAheadOfFbRemainder()
    {
        // Arrange
        var streams = new[]
        {
            Fb("https://streams.example.com/alpha", "Channel Alpha"),
            Fb("https://streams.example.com/bravo", "Channel Bravo"),
            Mp("https://mpoutqn.example.com/northgate", "Channel North")
        };
        var recommendations = new RecommendationResponse
        {
            Confidence = RecommendationConfidence.Medium,
            HasData = true,
            Recommended =
            [
                new RecommendationItem { Url = "https://mpoutqn.example.com/northgate" }
            ]
        };

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations,
            streams.Length,
            (url, _) => Array.FindIndex(streams, s => s.Url == url),
            index => streams[index]);

        // Assert
        Assert.Equal([2, 0, 1], order);
    }

    [Fact]
    public void Build_NoRecommendations_KeepsFbBeforeMpCatalogOrder()
    {
        // Arrange
        var streams = new[]
        {
            Mp("https://mpoutqn.example.com/east", "Channel East"),
            Fb("https://streams.example.com/north", "Channel North"),
            Fb("https://streams.example.com/south", "Channel South")
        };

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations: null,
            streams.Length,
            (_, _) => -1,
            index => streams[index]);

        // Assert
        Assert.Equal([1, 2, 0], order);
    }

    [Fact]
    public void Build_UnmatchedRecommendation_FallsBackToCatalogSourceOrder()
    {
        // Arrange
        var streams = new[]
        {
            Fb("https://streams.example.com/alpha", "Channel Alpha"),
            Mp("https://mpoutqn.example.com/east", "Channel East")
        };
        var recommendations = new RecommendationResponse
        {
            Confidence = RecommendationConfidence.High,
            HasData = true,
            Recommended =
            [
                new RecommendationItem { Url = "https://streams.example.com/missing" }
            ]
        };

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations,
            streams.Length,
            (_, _) => -1,
            index => streams[index]);

        // Assert
        Assert.Equal([0, 1], order);
    }

    [Fact]
    public void RankConfidence_OrdersHighAboveMediumAboveLow()
    {
        // Arrange
        const RecommendationConfidence high = RecommendationConfidence.High;
        const RecommendationConfidence medium = RecommendationConfidence.Medium;
        const RecommendationConfidence low = RecommendationConfidence.Low;
        const RecommendationConfidence missing = RecommendationConfidence.None;

        // Act
        var highRank = StreamTestOrderPolicy.RankConfidence(high);
        var mediumRank = StreamTestOrderPolicy.RankConfidence(medium);
        var lowRank = StreamTestOrderPolicy.RankConfidence(low);
        var missingRank = StreamTestOrderPolicy.RankConfidence(missing);

        // Assert
        Assert.True(highRank > mediumRank);
        Assert.True(mediumRank > lowRank);
        Assert.True(lowRank > missingRank);
    }

    [Fact]
    public void Build_HighConfidenceRecommendation_IsTriedBeforeLowConfidenceRecommendation()
    {
        // Arrange — API list can put a stale high-score stream first.
        var streams = new[]
        {
            Fb("https://streams.example.com/stale-strong", "Channel Stale"),
            Fb("https://streams.example.com/live-recent", "Channel Live"),
            Fb("https://streams.example.com/catalog-other", "Channel Other")
        };
        var recommendations = Recs(
            RecommendationConfidence.High,
            RecItem("https://streams.example.com/stale-strong", RecommendationConfidence.Low),
            RecItem("https://streams.example.com/live-recent", RecommendationConfidence.High));

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations,
            streams.Length,
            (url, _) => Array.FindIndex(streams, s => s.Url == url),
            index => streams[index]);

        // Assert
        Assert.Equal([1, 0, 2], order);
    }

    [Fact]
    public void Build_MediumSitsBetweenHighAndLow_ThenFbRemainderBeforeMp()
    {
        // Arrange
        var streams = new[]
        {
            Mp("https://mpoutqn.example.com/east", "Channel East"),
            Fb("https://streams.example.com/low", "Channel Low"),
            Fb("https://streams.example.com/remainder", "Channel Remainder"),
            Fb("https://streams.example.com/high", "Channel High"),
            Fb("https://streams.example.com/medium", "Channel Medium")
        };
        var recommendations = Recs(
            RecommendationConfidence.High,
            RecItem("https://streams.example.com/low", RecommendationConfidence.Low),
            RecItem("https://streams.example.com/medium", RecommendationConfidence.Medium),
            RecItem("https://streams.example.com/high", RecommendationConfidence.High));

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations,
            streams.Length,
            (url, _) => Array.FindIndex(streams, s => s.Url == url),
            index => streams[index]);

        // Assert
        Assert.Equal([3, 4, 1, 2, 0], order);
    }

    [Fact]
    public void Build_SameConfidence_PreservesApiIndex()
    {
        // Arrange
        var streams = new[]
        {
            Fb("https://streams.example.com/second", "Channel Second"),
            Fb("https://streams.example.com/first", "Channel First")
        };
        var recommendations = Recs(
            RecommendationConfidence.Low,
            RecItem("https://streams.example.com/second", RecommendationConfidence.Low),
            RecItem("https://streams.example.com/first", RecommendationConfidence.Low));

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations,
            streams.Length,
            (url, _) => Array.FindIndex(streams, s => s.Url == url),
            index => streams[index]);

        // Assert
        Assert.Equal([0, 1], order);
    }

    [Fact]
    public void Build_BlankAndDuplicateRecommendationUrls_AreSkipped()
    {
        // Arrange
        var streams = new[]
        {
            Fb("https://streams.example.com/northgate", "Channel North"),
            Fb("https://streams.example.com/other", "Channel Other")
        };
        var recommendations = Recs(
            RecommendationConfidence.High,
            RecItem("   ", RecommendationConfidence.High),
            RecItem("https://streams.example.com/northgate", RecommendationConfidence.High),
            RecItem("https://streams.example.com/northgate", RecommendationConfidence.Low));

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations,
            streams.Length,
            (url, _) => Array.FindIndex(streams, s => s.Url == url),
            index => streams[index]);

        // Assert
        Assert.Equal([0, 1], order);
    }

    [Fact]
    public void Build_ZeroStreams_ReturnsEmpty()
    {
        // Arrange
        var recommendations = Recs(
            RecommendationConfidence.High,
            RecItem("https://streams.example.com/northgate", RecommendationConfidence.High));

        // Act
        var order = StreamTestOrderPolicy.Build(
            recommendations,
            totalStreams: 0,
            (_, _) => 0,
            _ => throw new InvalidOperationException("no streams"));

        // Assert
        Assert.Empty(order);
    }

    private RecommendationItem RecItem(string url, RecommendationConfidence confidence) =>
        _fixture.Build<RecommendationItem>()
            .With(item => item.Url, url)
            .With(item => item.Confidence, confidence)
            .Without(item => item.StreamName)
            .Without(item => item.Meta)
            .Create();

    private RecommendationResponse Recs(
        RecommendationConfidence overall,
        params RecommendationItem[] items) =>
        _fixture.Build<RecommendationResponse>()
            .With(response => response.Confidence, overall)
            .With(response => response.HasData, items.Length > 0)
            .With(response => response.Recommended, items.ToList())
            .Create();

    private Stream Fb(string url, string channel) =>
        _fixture.Build<Stream>()
            .With(s => s.Url, url)
            .With(s => s.Channel, channel)
            .With(s => s.Source, "fb")
            .With(s => s.ResolutionStrategy, string.Empty)
            .With(s => s.PlayerStream, string.Empty)
            .With(s => s.StreamStatus, string.Empty)
            .Create();

    private Stream Mp(string url, string channel) =>
        _fixture.Build<Stream>()
            .With(s => s.Url, url)
            .With(s => s.Channel, channel)
            .With(s => s.Source, "mp")
            .With(s => s.ResolutionStrategy, "v2")
            .With(s => s.PlayerStream, channel)
            .With(s => s.PlayerStreams, new List<string> { channel })
            .With(s => s.StreamStatus, string.Empty)
            .Create();
}
