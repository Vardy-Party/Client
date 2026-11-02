using System.Collections.Generic;
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
            .With(s => s.ResolutionStrategy, "v1")
            .With(s => s.Source, "fb")
            .With(s => s.PlayerStream, (string?)null)
            .With(s => s.StreamStatus, "live")
            .Create();
        var current = _fixture.Build<EnrichedStream>()
            .With(e => e.Stream, stream)
            .With(e => e.Referer, "https://catalog.northgate.test/oak-lane")
            .Without(e => e.Health)
            .Without(e => e.RequestHeaders)
            .Without(e => e.RewrittenSegments)
            .Create();
        var response = _fixture.Build<M3U8Response>()
            .With(r => r.Url, "http://oak-lane.m3u8")
            .With(r => r.RequestHeaders, new Dictionary<string, string>
            {
                ["referer"] = "https://player.example.test/",
                ["cookie"] = "cdn=1"
            })
            .With(r => r.RewrittenSegments, (List<string>?)null)
            .Create();
        _fixture.GetMock<IApiService>()
            .Setup(api => api.GetM3U8UrlAsync(
                stream.Url,
                null,
                stream.ResolutionStrategy,
                stream.Source,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        var resolve = PlaybackUrlResolver.Bind(_fixture.GetMock<IApiService>().Object);

        // Act
        var url = await resolve(current, CancellationToken.None);

        // Assert
        Assert.Equal("http://oak-lane.m3u8", url);
        Assert.Equal("http://oak-lane.m3u8", current.ResolvedM3U8Url);
        Assert.Equal("https://player.example.test/", current.Referer);
        Assert.NotNull(current.RequestHeaders);
        Assert.Equal("cdn=1", current.RequestHeaders!["cookie"]);
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
            api => api.GetM3U8UrlAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
