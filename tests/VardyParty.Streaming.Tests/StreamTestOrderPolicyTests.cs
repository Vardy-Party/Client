using System;
using System.Collections.Generic;
using AutoFixture;
using VardyParty.Models;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Tests;

public class StreamTestOrderPolicyTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void ShouldPreferRecommendations_NonEmptyList_IsTrueEvenWhenConfidenceIsLow()
    {
        // Arrange
        var recommendations = new RecommendationResponse
        {
            Confidence = "low",
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
        var empty = new RecommendationResponse { Confidence = "high", HasData = true };
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
            Confidence = "low",
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
            Confidence = "medium",
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
            Confidence = "high",
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
