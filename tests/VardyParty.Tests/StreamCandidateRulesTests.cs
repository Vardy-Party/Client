using AutoFixture;
using VardyParty.Resolvers;
using Xunit;

namespace VardyParty.Tests;

public class StreamCandidateRulesTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void ShouldSkipCountdown_SkipsPlayableProbe()
    {
        // Arrange
        const bool countdown = true;
        const bool playable = false;

        // Act
        var skipCountdown = StreamCandidateRules.ShouldSkipCountdown(countdown);
        var keepPlayable = StreamCandidateRules.ShouldSkipCountdown(playable);

        // Assert
        Assert.True(skipCountdown);
        Assert.False(keepPlayable);
    }

    [Fact]
    public void ShouldAcceptFreshM3U8_WhenUrlRotated_ReturnsTrue()
    {
        // Arrange
        var failedCachedUrl = _fixture.Create<string>();
        var freshUrl = _fixture.Create<string>();

        // Act
        var shouldAccept = StreamCandidateRules.ShouldAcceptFreshM3U8(failedCachedUrl, freshUrl);

        // Assert
        Assert.True(shouldAccept);
    }
}
