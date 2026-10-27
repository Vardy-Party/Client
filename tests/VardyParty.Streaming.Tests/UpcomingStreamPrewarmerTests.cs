using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Streaming;
using Xunit;
using static VardyParty.Streaming.Tests.WarmPlaybackUrlCacheTests;

namespace VardyParty.Streaming.Tests;

public class UpcomingStreamPrewarmerTests
{
    private readonly Mock<IApiService> _api = new();
    private readonly WarmPlaybackUrlCache _cache = new();
    private readonly ManualTime _time = new(Now);
    private readonly StreamSwitchingService _pool = new();
    private readonly List<string> _resolved = new();

    public UpcomingStreamPrewarmerTests()
    {
        _pool.Initialize("northgate-league", "Oak Lane", "Northgate");
    }

    private UpcomingStreamPrewarmer CreateSut() => new(
        _api.Object,
        _pool,
        _cache,
        NullLogger<UpcomingStreamPrewarmer>.Instance,
        _time);

    private void Returns(string pageUrl, string m3u8)
        => _api.Setup(a => a.GetM3U8UrlAsync(pageUrl, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _resolved.Add(pageUrl))
            .ReturnsAsync(new M3U8Response { Url = m3u8 });

    [Fact]
    public async Task TickAsync_WarmsImmediateNextOneShotRow_NeverCurrent()
    {
        // Arrange
        var current = SoakRow("current");
        var next = SoakRow("next");
        _pool.AddHealthyStream(current);
        _pool.AddHealthyStream(next);
        Returns(next.Stream.Url, UzhiUrl);
        var sut = CreateSut();

        // Act
        var issued = await sut.TickAsync(CancellationToken.None);

        // Assert
        Assert.True(issued);
        Assert.Equal(new[] { next.Stream.Url }, _resolved);
        Assert.Equal(UzhiUrl, _cache.Peek(next)!.Response.Url);
        Assert.Null(_cache.Peek(current));
    }

    [Fact]
    public async Task TickAsync_SecondTickWarmsDepthTwo_ThenIdlesWhileWarm()
    {
        // Arrange
        var current = SoakRow("current");
        var next = SoakRow("next");
        var after = SoakRow("after");
        _pool.AddHealthyStream(current);
        _pool.AddHealthyStream(next);
        _pool.AddHealthyStream(after);
        Returns(next.Stream.Url, UzhiUrl);
        Returns(after.Stream.Url, UzhiUrl.Replace("tims-nl-0001", "tims-nl-0002"));
        var sut = CreateSut();

        // Act
        await sut.TickAsync(CancellationToken.None);
        await sut.TickAsync(CancellationToken.None);
        var third = await sut.TickAsync(CancellationToken.None);

        // Assert
        Assert.Equal(new[] { next.Stream.Url, after.Stream.Url }, _resolved);
        Assert.False(third);
    }

    [Fact]
    public async Task TickAsync_RefreshesImmediateNextBeforeExpiry()
    {
        // Arrange
        var current = SoakRow("current");
        var next = SoakRow("next");
        _pool.AddHealthyStream(current);
        _pool.AddHealthyStream(next);
        Returns(next.Stream.Url, CoweddUrl(1793137120));
        var sut = CreateSut();
        await sut.TickAsync(CancellationToken.None);

        // Act - 40s later only 80s left (< 60s + 30s lead).
        _time.Now = Now.AddSeconds(40);
        var refreshed = await sut.TickAsync(CancellationToken.None);

        // Assert
        Assert.True(refreshed);
        Assert.Equal(2, _resolved.Count);
    }

    [Fact]
    public async Task TickAsync_IsSingleFlight()
    {
        // Arrange
        var current = SoakRow("current");
        var next = SoakRow("next");
        var after = SoakRow("after");
        _pool.AddHealthyStream(current);
        _pool.AddHealthyStream(next);
        _pool.AddHealthyStream(after);
        var gate = new TaskCompletionSource<M3U8Response?>();
        var calls = 0;
        _api.Setup(a => a.GetM3U8UrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref calls))
            .Returns(gate.Task);
        var sut = CreateSut();

        // Act
        var first = sut.TickAsync(CancellationToken.None);
        var second = await sut.TickAsync(CancellationToken.None);
        gate.SetResult(new M3U8Response { Url = UzhiUrl });
        await first;

        // Assert
        Assert.False(second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TickAsync_SkipsKnownSingleUseAndPlainRows()
    {
        // Arrange
        var current = SoakRow("current");
        var cookie = SoakRow("cookie");
        cookie.RequestHeaders = new Dictionary<string, string> { ["Cookie"] = "fb=1" };
        var plain = SoakRow("plain");
        plain.RequestHeaders = null;
        _pool.AddHealthyStream(current);
        _pool.AddHealthyStream(cookie);
        _pool.AddHealthyStream(plain);
        var sut = CreateSut();

        // Act
        var issued = await sut.TickAsync(CancellationToken.None);

        // Assert
        Assert.False(issued);
        _api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TickAsync_NoReadableExpiry_IsNotRetried()
    {
        // Arrange
        var current = SoakRow("current");
        var next = SoakRow("next");
        _pool.AddHealthyStream(current);
        _pool.AddHealthyStream(next);
        Returns(next.Stream.Url, "https://cdn.next.test/live.m3u8?sig=abc");
        var sut = CreateSut();

        // Act
        await sut.TickAsync(CancellationToken.None);
        var again = await sut.TickAsync(CancellationToken.None);

        // Assert
        Assert.False(again);
        Assert.Single(_resolved);
        Assert.True(_cache.IsUnwarmable(next));
    }

    [Fact]
    public async Task Start_DisposeStopsLoop()
    {
        // Arrange
        var current = SoakRow("current");
        var next = SoakRow("next");
        _pool.AddHealthyStream(current);
        _pool.AddHealthyStream(next);
        Returns(next.Stream.Url, UzhiUrl);
        var sut = CreateSut();

        // Act
        using (sut.Start(CancellationToken.None))
        {
            for (var i = 0; i < 50 && _cache.Peek(next) == null; i++)
                await Task.Delay(20);
        }

        // Assert
        Assert.NotNull(_cache.Peek(next));
    }

    internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
