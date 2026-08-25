using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Tests;

public class DelegatingMediaEngineTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public async Task AttachAsync_InvokesHandler_WithUrlAndHeaders()
    {
        // Arrange
        var url = _fixture.Create<string>();
        var headerName = _fixture.Create<string>();
        var headerValue = _fixture.Create<string>();
        IReadOnlyDictionary<string, string>? capturedHeaders = null;
        string? capturedUrl = null;
        var engine = new DelegatingMediaEngine
        {
            AttachHandler = (mediaUrl, headers, _) =>
            {
                capturedUrl = mediaUrl;
                capturedHeaders = headers;
                return Task.CompletedTask;
            }
        };

        // Act
        await engine.AttachAsync(url, new Dictionary<string, string> { [headerName] = headerValue });

        // Assert
        Assert.Equal(url, capturedUrl);
        Assert.Equal(headerValue, capturedHeaders![headerName]);
    }

    [Fact]
    public void Raise_ForwardsEngineEvent()
    {
        // Arrange
        var engine = new DelegatingMediaEngine();
        MediaEngineEvent? captured = null;
        engine.EngineEvent += (_, e) => captured = e;
        var generation = _fixture.Create<long>();

        // Act
        engine.Raise(MediaEngineEvent.Ready(generation));

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(MediaEngineEventKind.Ready, captured!.Kind);
        Assert.Equal(generation, captured.Generation);
    }

    [Fact]
    public async Task StopAsync_WhenNoHandler_Completes()
    {
        // Arrange
        var engine = new DelegatingMediaEngine();

        // Act
        await engine.StopAsync(CancellationToken.None);

        // Assert
        Assert.Null(engine.GetCurrentMetrics());
    }
}
