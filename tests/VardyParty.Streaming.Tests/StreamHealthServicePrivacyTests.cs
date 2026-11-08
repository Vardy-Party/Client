using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VardyParty.Kernel;
using VardyParty.Ports;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class StreamHealthServicePrivacyTests
{
    [Fact]
    public async Task ReportHealthAsync_WhenOptedOut_DoesNotPost()
    {
        // Arrange
        var posts = 0;
        var handler = new ScriptHandler((_, _) =>
        {
            Interlocked.Increment(ref posts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var prefs = new InMemoryStreamHealthDataCollectionPreferencesStore();
        prefs.SaveStreamHealthDataCollectionEnabled(false);
        var sut = CreateService(handler, prefs);

        // Act
        await sut.ReportHealthAsync(
            "League Alpha",
            "Home United",
            "Away City",
            new StreamHealthReport
            {
                StreamUrl = "https://streams.example.com/channel-north",
                Status = "working",
                SessionId = "session-north",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

        // Assert
        Assert.Equal(0, posts);
    }

    [Fact]
    public async Task ReportHealthAsync_WhenOptedIn_PostsHealth()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        var handler = new ScriptHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var prefs = new InMemoryStreamHealthDataCollectionPreferencesStore();
        prefs.SaveStreamHealthDataCollectionEnabled(true);
        var sut = CreateService(handler, prefs);

        // Act
        await sut.ReportHealthAsync(
            "League Alpha",
            "Home United",
            "Away City",
            new StreamHealthReport
            {
                StreamUrl = "https://streams.example.com/channel-north",
                Status = "working",
                SessionId = "session-north",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("/health", captured.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportHealthAsync_WhenOptedOut_StillPostsUserReport()
    {
        // Arrange
        HttpRequestMessage? captured = null;
        var handler = new ScriptHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var prefs = new InMemoryStreamHealthDataCollectionPreferencesStore();
        prefs.SaveStreamHealthDataCollectionEnabled(false);
        var sut = CreateService(handler, prefs);

        // Act
        await sut.ReportHealthAsync(
            "League Alpha",
            "Home United",
            "Away City",
            new StreamHealthReport
            {
                StreamUrl = "https://streams.example.com/channel-north",
                Status = "user-report",
                Error = "picture frozen",
                SessionId = "session-north",
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("/health", captured.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    private static StreamHealthService CreateService(
        HttpMessageHandler handler,
        IStreamHealthDataCollectionPreferencesStore prefs) =>
        new(
            new HttpClient(handler),
            Options.Create(new APISettings { HeadlessBaseUrl = "https://api.example.test" }),
            prefs,
            NullLogger<StreamHealthService>.Instance);

    private sealed class ScriptHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            next(request, cancellationToken);
    }
}
