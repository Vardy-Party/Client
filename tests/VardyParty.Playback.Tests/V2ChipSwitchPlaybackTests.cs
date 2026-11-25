using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Regression for the TV log of 13 Nov 2026 (Home United v Away City, 18:40:50-18:41:46):
/// Next onto MP chips probed Healthy (Chip North, Chip West) asked LocalService for a fresh link,
/// got "POST /mp sent nothing for 30s", and stayed on Chip Home. Jon: "i am used to v2 switching
/// being immediate". A V2 chip with a usable probed link must switch with no LocalService
/// call; a switch whose target cannot be resolved must not silently stay put.
/// </summary>
public class V2ChipSwitchPlaybackTests
{
    private const string HomeChipUrl = "https://edge.cdn-chip.test/token-LU4q/cfall/s2001/v3b/4qqp/super/3x_942237.m3u8";
    private const string NorthChipUrl = "https://edge.cdn-chip.test/token-LBV6/cfall/s2001/v3b/s538/hedy/sx_944566.m3u8";

    private readonly StreamSwitchingService _pool = new();
    private readonly PlaybackSessionController _session = new();
    private readonly List<(string Url, bool UsedCached)> _attaches = new();
    private readonly List<string> _localServiceCalls = new();
    private readonly List<string> _status = new();
    private readonly List<PlaybackCommand> _commands = new();
    private Func<EnrichedStream, CancellationToken, Task<string?>> _resolve = (_, _) => Task.FromResult<string?>(null);

    public V2ChipSwitchPlaybackTests()
    {
        _pool.Initialize("Fictional League", "Home United", "Away City");
    }

    private static EnrichedStream Chip(string chip, string? url) => new()
    {
        Stream = new Stream
        {
            Url = "https://www.match-page.test/football/home-united-vs-away-city.html",
            Channel = chip,
            PlayerStream = chip,
            Source = "mp",
            ResolutionStrategy = "v2",
        },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
        RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://chip-player.test/" },
        UnusedResolvedUrl = false,
    };

    private PlaybackPoolCommandActions CreateSut()
        => new(
            _session,
            _pool,
            (row, ct) =>
            {
                _localServiceCalls.Add(row.Stream.Channel);
                return _resolve(row, ct);
            },
            (url, usedCached, _) => _attaches.Add((url, usedCached)),
            _commands.Add);

    private Task<bool> SwitchAsync(PlaybackPoolCommandActions sut)
        => sut.SwitchToCurrentAsync(false, _status.Add, revert => revert());

    [Fact]
    public async Task Rule_NextOntoProbedHealthyV2Chip_UnknownExpiry_SwitchesImmediately_WithNoLocalServiceCall()
    {
        // Arrange - Chip Home plays; Chip North was probed Healthy (unreadable token, Referer soak).
        var chipHome = Chip("Chip Home", HomeChipUrl);
        var chipNorth = Chip("Chip North", NorthChipUrl);
        _pool.AddHealthyStream(chipHome);
        _pool.AddHealthyStream(chipNorth);
        _session.BeginAttach(HomeChipUrl, usedCachedUrl: true);
        _pool.SwitchToNextStream();
        _resolve = (_, _) => throw new InvalidOperationException("POST /mp sent nothing for 30s; chip Chip North not harvested yet");
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert
        Assert.True(switched);
        Assert.Empty(_localServiceCalls);
        Assert.Equal(new[] { (NorthChipUrl, true) }, _attaches);
        Assert.Empty(_status);
    }

    [Fact]
    public async Task Rule_AutoAdvanceOntoProbedV2Chip_AttachesProbedLink_WithNoLocalServiceCall()
    {
        // Arrange - the playing row was removed after a failure; the pool's current row is Chip North.
        var chipNorth = Chip("Chip North", NorthChipUrl);
        _pool.AddHealthyStream(chipNorth);
        var sut = CreateSut();

        // Act
        await sut.AttachCurrentFromPoolAsync();

        // Assert
        Assert.Empty(_localServiceCalls);
        Assert.Equal(new[] { (NorthChipUrl, true) }, _attaches);
    }

    [Fact]
    public async Task Rule_NextWhoseTargetCannotBeResolved_MovesOnToTheNextPlayableRow()
    {
        // Arrange - Chip Home plays; Chip West has no link and its fresh /mp fails; Chip East is playable.
        var chipHome = Chip("Chip Home", HomeChipUrl);
        var chipWest = Chip("Chip West", null);
        var chipEast = Chip("Chip East", NorthChipUrl);
        _pool.AddHealthyStream(chipHome);
        _pool.AddHealthyStream(chipWest);
        _pool.AddHealthyStream(chipEast);
        _session.BeginAttach(HomeChipUrl, usedCachedUrl: true);
        _pool.SwitchToNextStream();
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert - not a switch, the user is told, Chip West is skipped this cycle, Next goes on.
        Assert.False(switched);
        Assert.Equal(new[] { "Chip West" }, _localServiceCalls);
        Assert.Empty(_attaches);
        Assert.Same(chipHome, _pool.GetCurrentStream());
        Assert.True(_pool.IsSkippedInCycle(chipWest));
        Assert.Contains("trying the next stream", _status[^1]);
        var command = Assert.Single(_commands);
        Assert.True(command.SwitchPoolToNext);
        // Host applies SwitchPoolToNext: the pool skips Chip West (tried this cycle) and lands on Chip East.
        Assert.True(_pool.SwitchToNextStream());
        Assert.Same(chipEast, _pool.GetCurrentStream());
    }

    [Fact]
    public async Task Rule_NextWhoseTargetCannotBeResolved_StaysOnlyWhenNothingElseIsLeft()
    {
        // Arrange
        var chipHome = Chip("Chip Home", HomeChipUrl);
        var chipWest = Chip("Chip West", null);
        _pool.AddHealthyStream(chipHome);
        _pool.AddHealthyStream(chipWest);
        _session.BeginAttach(HomeChipUrl, usedCachedUrl: true);
        _pool.SwitchToNextStream();
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert
        Assert.False(switched);
        Assert.Empty(_attaches);
        Assert.Empty(_commands);
        Assert.Same(chipHome, _pool.GetCurrentStream());
        Assert.Contains("staying on the current stream", _status[^1]);
    }
}
