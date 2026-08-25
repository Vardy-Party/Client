using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Moq;
using VardyParty.Hosting;
using VardyParty.Kernel;
using VardyParty.Streaming;
using Xunit;
using VardyParty.TestSupport;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Hosting.Tests;

public class PlaybackUrlResolverTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task Bind_WhenStreamPresent_DelegatesToApi()
    {
        // Arrange
        var stream = _fixture.Build<StreamModel>()
            .With(s => s.Url, "https://streams.example.com/northgate")
            .With(s => s.Channel, "Channel North")
            .Create();
        var current = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.Referer, "https://catalog.northgate.test/oak-lane")
            .Without(e => e.Health)
            .Create();
        _fixture.GetMock<IApiService>()
            .Setup(api => api.ResolveM3U8ForPlaybackAsync(
                stream,
                "https://catalog.northgate.test/oak-lane",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://oak-lane.m3u8");
        var resolve = PlaybackUrlResolver.Bind(_fixture.GetMock<IApiService>().Object);

        // Act
        var url = await resolve(current, CancellationToken.None);

        // Assert
        Assert.Equal("http://oak-lane.m3u8", url);
    }

    [Fact]
    public async Task Bind_WhenStreamMissing_DoesNotCallApi()
    {
        // Arrange
        var current = _fixture.Build<EnrichedStream>()
            .Without(e => e.Health)
            .Create();
        current.Stream = null!;
        var resolve = PlaybackUrlResolver.Bind(_fixture.GetMock<IApiService>().Object);

        // Act
        var url = await resolve(current, CancellationToken.None);

        // Assert
        Assert.Null(url);
        _fixture.GetMock<IApiService>().Verify(
            api => api.ResolveM3U8ForPlaybackAsync(
                It.IsAny<StreamModel>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
