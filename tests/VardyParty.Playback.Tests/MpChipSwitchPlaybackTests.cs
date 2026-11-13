using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Regression for the TV log of 13 Nov 2026 (Moldova v Kazakhstan, 18:40:50-18:41:46):
/// Next onto MP chips probed Healthy (Fubo US, NW IT) asked LocalService for a fresh link,
/// got "POST /mp sent nothing for 30s", and stayed on VUS. Jon: "i am used to v2 switching
/// being immediate". A V2 chip with a usable probed link must switch with no LocalService
/// call; a switch whose target cannot be resolved must not silently stay put.
/// </summary>
public class MpChipSwitchPlaybackTests
{
    private const string VusUrl = "https://fhlsport955.cdx904gouh.xyz/token-LU4q/cfall/s2001/v3b/4qqp/super/3x_942237.m3u8";
    private const string FuboUrl = "https://fhlsport955.cdx904gouh.xyz/token-LBV6/cfall/s2001/v3b/s538/hedy/sx_944566.m3u8";

    private readonly StreamSwitchingService _pool = new();
    private readonly PlaybackSessionController _session = new();
    private readonly List<(string Url, bool UsedCached)> _attaches = new();
    private readonly List<string> _localServiceCalls = new();
    private readonly List<string> _status = new();
    private readonly List<PlaybackCommand> _commands = new();
    private Func<EnrichedStream, CancellationToken, Task<string?>> _resolve = (_, _) => Task.FromResult<string?>(null);

    public MpChipSwitchPlaybackTests()
    {
        _pool.Initialize("UEFA Nations League", "Moldova", "Kazakhstan");
    }

    private static EnrichedStream Chip(string chip, string? url) => new()
    {
        Stream = new Stream
        {
            Url = "https://www.fctv33go.autos/football/moldova-vs-kazakhstan.html",
            Channel = chip,
            PlayerStream = chip,
            Source = "mp",
            ResolutionStrategy = "v2",
        },
        Status = StreamResolutionStatus.Healthy,
        ResolvedM3U8Url = url,
        RequestHeaders = new Dictionary<string, string> { ["Referer"] = "https://vida01eo.example.sbs/" },
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
    public async Task Rule_NextOntoProbedHealthyMpChip_UnknownExpiry_SwitchesImmediately_WithNoLocalServiceCall()
    {
        // Arrange - VUS plays; Fubo US was probed Healthy (unreadable token, Referer soak).
        var vus = Chip("VUS", VusUrl);
        var fubo = Chip("Fubo US", FuboUrl);
        _pool.AddHealthyStream(vus);
        _pool.AddHealthyStream(fubo);
        _session.BeginAttach(VusUrl, usedCachedUrl: true);
        _pool.SwitchToNextStream();
        _resolve = (_, _) => throw new InvalidOperationException("POST /mp sent nothing for 30s; chip Fubo US not harvested yet");
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert
        Assert.True(switched);
        Assert.Empty(_localServiceCalls);
        Assert.Equal(new[] { (FuboUrl, true) }, _attaches);
        Assert.Empty(_status);
    }

    [Fact]
    public async Task Rule_AutoAdvanceOntoProbedMpChip_AttachesProbedLink_WithNoLocalServiceCall()
    {
        // Arrange - the playing row was removed after a failure; the pool's current row is Fubo US.
        var fubo = Chip("Fubo US", FuboUrl);
        _pool.AddHealthyStream(fubo);
        var sut = CreateSut();

        // Act
        await sut.AttachCurrentFromPoolAsync();

        // Assert
        Assert.Empty(_localServiceCalls);
        Assert.Equal(new[] { (FuboUrl, true) }, _attaches);
    }

    [Fact]
    public async Task Rule_NextWhoseTargetCannotBeResolved_MovesOnToTheNextPlayableRow()
    {
        // Arrange - VUS plays; NW IT has no link and its fresh /mp fails; TyR is playable.
        var vus = Chip("VUS", VusUrl);
        var nwIt = Chip("NW IT", null);
        var tyr = Chip("TyR", FuboUrl);
        _pool.AddHealthyStream(vus);
        _pool.AddHealthyStream(nwIt);
        _pool.AddHealthyStream(tyr);
        _session.BeginAttach(VusUrl, usedCachedUrl: true);
        _pool.SwitchToNextStream();
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert - not a switch, the user is told, NW IT is skipped this cycle, Next goes on.
        Assert.False(switched);
        Assert.Equal(new[] { "NW IT" }, _localServiceCalls);
        Assert.Empty(_attaches);
        Assert.Same(vus, _pool.GetCurrentStream());
        Assert.True(_pool.IsSkippedInCycle(nwIt));
        Assert.Contains("trying the next stream", _status[^1]);
        var command = Assert.Single(_commands);
        Assert.True(command.SwitchPoolToNext);
        // Host applies SwitchPoolToNext: the pool skips NW IT (tried this cycle) and lands on TyR.
        Assert.True(_pool.SwitchToNextStream());
        Assert.Same(tyr, _pool.GetCurrentStream());
    }

    [Fact]
    public async Task Rule_NextWhoseTargetCannotBeResolved_StaysOnlyWhenNothingElseIsLeft()
    {
        // Arrange
        var vus = Chip("VUS", VusUrl);
        var nwIt = Chip("NW IT", null);
        _pool.AddHealthyStream(vus);
        _pool.AddHealthyStream(nwIt);
        _session.BeginAttach(VusUrl, usedCachedUrl: true);
        _pool.SwitchToNextStream();
        var sut = CreateSut();

        // Act
        var switched = await SwitchAsync(sut);

        // Assert
        Assert.False(switched);
        Assert.Empty(_attaches);
        Assert.Empty(_commands);
        Assert.Same(vus, _pool.GetCurrentStream());
        Assert.Contains("staying on the current stream", _status[^1]);
    }
}
