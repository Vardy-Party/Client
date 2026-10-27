using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Unit + business rules for make-before-break switching and expiry-aware reuse
/// (STREAM_PLAYBACK_RULES.md "Switch speed: reuse, pre-resolve, make-before-break").
/// </summary>
public class PlaybackSwitchMakeBeforeBreakTests
{
    private readonly StreamSwitchingService _pool = new();
    private readonly PlaybackSessionController _session = new();
    private readonly List<(string Url, bool UsedCached)> _attaches = new();
    private readonly List<string> _resolved = new();
    private readonly List<string> _status = new();
    private int _suppressedReverts;
    private Func<EnrichedStream, CancellationToken, Task<string?>> _resolve = (_, _) => Task.FromResult<string?>(null);

    public PlaybackSwitchMakeBeforeBreakTests()
    {
        _pool.Initialize("northgate-league", "Oak Lane", "Northgate");
    }

    private static string LongLivedUrl(string channel)
        => $"https://tims-nl-0001.uzhi.ru/main/secure/{new string('a', 64)}/{DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()}/{channel}.m3u8";

    private static EnrichedStream SoakRow(string channel, string? resolved, Dictionary<string, string>? headers = null)
        => new()
        {
            Stream = new Stream { Url = "https://northgate.test/" + channel, Channel = channel },
            Status = StreamResolutionStatus.Healthy,
            ResolvedM3U8Url = resolved,
            RequestHeaders = headers ?? new Dictionary<string, string> { ["Referer"] = "https://player.northgate.test/" + channel },
        };

    private PlaybackPoolCommandActions CreateSut(bool omitResolver = false)
        => new(
            _session,
            _pool,
            omitResolver ? null : (row, ct) =>
            {
                _resolved.Add(row.Stream.Channel);
                return _resolve(row, ct);
            },
            (url, usedCached, _) => _attaches.Add((url, usedCached)),
            _ => { });

    private Task<bool> SwitchAsync(PlaybackPoolCommandActions sut, bool force = false, CancellationToken ct = default)
        => sut.SwitchToCurrentAsync(force, _status.Add, revert =>
        {
            _suppressedReverts++;
            revert();
        }, ct);

    private (EnrichedStream Playing, EnrichedStream Target) PlayingThenNext(string? targetUrl, Dictionary<string, string>? targetHeaders = null)
    {
        var playing = SoakRow("soccermlbstream", "https://cdn.soccer.test/live.m3u8?sig=1");
        var target = SoakRow("vivoentv", targetUrl, targetHeaders);
        _pool.AddHealthyStream(playing);
        _pool.AddHealthyStream(target);
        _session.BeginAttach(playing.ResolvedM3U8Url!, usedCachedUrl: false);
        _pool.SwitchToNextStream();
        return (playing, target);
    }

    [Fact]
    public async Task Switch_ReusableToken_AttachesCachedImmediately_NoPlay()
    {
        // Arrange
        var url = LongLivedUrl("vivoentv");
        PlayingThenNext(url);
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert
        Assert.True(switched);
        Assert.Empty(_resolved);
        Assert.Equal(new[] { (url, true) }, _attaches);
        Assert.Empty(_status);
    }

    [Fact]
    public async Task Switch_UnknownExpiry_ShowsPendingThenAttachesFresh()
    {
        // Arrange
        PlayingThenNext("http://oak-lane-burned.m3u8");
        _resolve = (_, _) => Task.FromResult<string?>("https://fresh.vivoentv.test/a.m3u8");
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert
        Assert.True(switched);
        Assert.Equal(new[] { "vivoentv" }, _resolved);
        Assert.Equal(new[] { ("https://fresh.vivoentv.test/a.m3u8", false) }, _attaches);
        Assert.Equal(new[] { "Switching to vivoentv\u2026" }, _status);
    }

    [Fact]
    public async Task Switch_CookieBoundRow_StaysOnFreshPathEvenWithLongToken()
    {
        // Arrange
        PlayingThenNext(LongLivedUrl("vivoentv"), new Dictionary<string, string> { ["Cookie"] = "fb=1" });
        _resolve = (_, _) => Task.FromResult<string?>("https://fresh.vivoentv.test/b.m3u8");
        var sut = CreateSut();

        // Act
        await SwitchAsync(sut);

        // Assert
        Assert.Single(_resolved);
        Assert.False(_attaches[0].UsedCached);
    }

    [Fact]
    public async Task Rule_SwitchFails_StaysOnPlayingStream_WithoutReplayingIt()
    {
        // Arrange
        var (playing, _) = PlayingThenNext("http://oak-lane-burned.m3u8");
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert
        Assert.False(switched);
        Assert.Empty(_attaches);
        Assert.Equal(new[] { "vivoentv" }, _resolved);
        Assert.Same(playing, _pool.GetCurrentStream());
        Assert.Equal(1, _suppressedReverts);
        Assert.Contains("staying on the current stream", _status[^1]);
    }

    [Fact]
    public async Task Switch_ResolveThrows_IsTreatedAsFailedSwitch()
    {
        // Arrange
        var (playing, _) = PlayingThenNext("http://oak-lane-burned.m3u8");
        _resolve = (_, _) => throw new InvalidOperationException("northgate resolve");
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert
        Assert.False(switched);
        Assert.Same(playing, _pool.GetCurrentStream());
    }

    [Fact]
    public async Task Switch_FailsWithNothingPlaying_FallsBackToCachedUsedCached()
    {
        // Arrange
        var target = SoakRow("vivoentv", "http://oak-lane-burned.m3u8");
        _pool.AddHealthyStream(target);
        var sut = CreateSut();

        // Act
        await SwitchAsync(sut);

        // Assert
        Assert.Equal(new[] { ("http://oak-lane-burned.m3u8", true) }, _attaches);
        Assert.Equal(0, _suppressedReverts);
    }

    [Fact]
    public async Task Switch_SupersededWhileResolving_DoesNotAttachOrRevert()
    {
        // Arrange
        var (playing, _) = PlayingThenNext("http://oak-lane-burned.m3u8");
        var third = SoakRow("all-sports-hd", "http://all-sports.m3u8");
        _pool.AddHealthyStream(third);
        var gate = new TaskCompletionSource<string?>();
        _resolve = (_, _) => gate.Task;
        var sut = CreateSut();

        // Act
        var pending = SwitchAsync(sut);
        _pool.SwitchToNextStream();
        gate.SetResult("https://fresh.vivoentv.test/c.m3u8");
        var switched = await pending;

        // Assert
        Assert.False(switched);
        Assert.Empty(_attaches);
        Assert.Equal(0, _suppressedReverts);
        Assert.NotSame(playing, _pool.GetCurrentStream());
    }

    [Fact]
    public async Task Switch_CachedUrlAlreadyPlaying_IsNoOpUnlessForced()
    {
        // Arrange
        var url = LongLivedUrl("vivoentv");
        var row = SoakRow("vivoentv", url);
        _pool.AddHealthyStream(row);
        _session.BeginAttach(url, usedCachedUrl: false);
        var sut = CreateSut();

        // Act
        var plain = await SwitchAsync(sut);
        var forced = await SwitchAsync(sut, force: true);

        // Assert
        Assert.False(plain);
        Assert.True(forced);
        Assert.Single(_attaches);
    }

    [Fact]
    public async Task Switch_NoResolverWired_AttachesCached()
    {
        // Arrange
        PlayingThenNext("http://oak-lane-burned.m3u8");
        var sut = CreateSut(omitResolver: true);

        // Act
        await SwitchAsync(sut);

        // Assert
        Assert.Equal(new[] { ("http://oak-lane-burned.m3u8", true) }, _attaches);
    }

    [Fact]
    public async Task AttachCurrentFromPool_ReusableToken_SkipsPlay()
    {
        // Arrange
        var url = LongLivedUrl("vivoentv");
        _pool.AddHealthyStream(SoakRow("vivoentv", url));
        var sut = CreateSut();

        // Act
        await sut.AttachCurrentFromPoolAsync();

        // Assert
        Assert.Empty(_resolved);
        Assert.Equal(new[] { (url, true) }, _attaches);
    }

    [Fact]
    public void FindPlayingRow_MatchesSessionUrlExcludingTarget()
    {
        // Arrange
        var (playing, target) = PlayingThenNext("http://oak-lane-burned.m3u8");
        var sut = CreateSut();

        // Assert
        Assert.Same(playing, sut.FindPlayingRow(playing.ResolvedM3U8Url, target));
        Assert.Null(sut.FindPlayingRow(playing.ResolvedM3U8Url, playing));
        Assert.Null(sut.FindPlayingRow(null, null));
    }
}
