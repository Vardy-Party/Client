using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VardyParty.Streaming;

/// <summary>
/// Wall-clock markers for the stall between "Finding streams… N healthy" and ExoPlayer open.
/// Logged as <c>[FirstPlayDelay]</c> so adb/logcat can filter one tag.
/// </summary>
internal sealed class FirstPlayDelayDiag
{
    private readonly Stopwatch _sinceSession = Stopwatch.StartNew();
    private long? _msAtFirstHealthy;
    private long? _msAtSecondHealthy;
    private long? _msAtPlaybackChain;
    private long? _msAtRaceStart;
    private long? _msAtRaceWon;
    private string? _firstHealthyChannel;
    private string? _secondHealthyChannel;
    private bool _playerOpenedLogged;

    /// <summary>The session's <c>[probe-timing]</c> trace (picture, backup, switches).</summary>
    public ProbeSessionTrace? Trace { get; set; }

    public long? MsSinceFirstHealthy =>
        _msAtFirstHealthy is { } t ? _sinceSession.ElapsedMilliseconds - t : null;

    public long? MsSinceSecondHealthy =>
        _msAtSecondHealthy is { } t ? _sinceSession.ElapsedMilliseconds - t : null;

    public void MarkHealthyCount(int healthyCount, string? channel, ILogger logger)
    {
        if (healthyCount == 1 && _msAtFirstHealthy is null)
        {
            _msAtFirstHealthy = _sinceSession.ElapsedMilliseconds;
            _firstHealthyChannel = channel;
            logger.LogInformation(
                "[FirstPlayDelay] healthy=1 channel={Channel} sessionMs={SessionMs}",
                channel, _msAtFirstHealthy);
        }
        else if (healthyCount == 2 && _msAtSecondHealthy is null)
        {
            _msAtSecondHealthy = _sinceSession.ElapsedMilliseconds;
            _secondHealthyChannel = channel;
            logger.LogInformation(
                "[FirstPlayDelay] healthy=2 channel={Channel} sessionMs={SessionMs} msAfter1st={MsAfter1st} " +
                "(finding UI can show TWO HEALTHY — picture not open yet)",
                channel,
                _msAtSecondHealthy,
                _msAtSecondHealthy - _msAtFirstHealthy);
        }
        else if (healthyCount >= 2)
        {
            logger.LogInformation(
                "[FirstPlayDelay] healthy={Count} channel={Channel} sessionMs={SessionMs} " +
                "msSince2ndHealthy={MsSince2nd} playerOpened={Opened}",
                healthyCount,
                channel,
                _sinceSession.ElapsedMilliseconds,
                MsSinceSecondHealthy,
                _playerOpenedLogged);
        }
    }

    public void MarkPlaybackChainStarted(string? channel)
    {
        _msAtPlaybackChain ??= _sinceSession.ElapsedMilliseconds;
        // Channel retained for race/player logs; no log here (race log is the useful one).
        _ = channel;
    }

    public void MarkRaceStarted(ILogger logger)
    {
        _msAtRaceStart ??= _sinceSession.ElapsedMilliseconds;
        logger.LogInformation(
            "[FirstPlayDelay] race_start sessionMs={SessionMs} msSince1st={Ms1} msSince2nd={Ms2} " +
            "first={First} second={Second}",
            _msAtRaceStart,
            MsSinceFirstHealthy,
            MsSinceSecondHealthy,
            _firstHealthyChannel,
            _secondHealthyChannel);
    }

    public void MarkRaceWon(string? winner, ILogger logger)
    {
        _msAtRaceWon ??= _sinceSession.ElapsedMilliseconds;
        var raceMs = _msAtRaceStart is { } start
            ? _msAtRaceWon.Value - start
            : (long?)null;
        logger.LogInformation(
            "[FirstPlayDelay] race_won winner={Winner} raceMs={RaceMs} sessionMs={SessionMs} " +
            "msSince2ndHealthy={MsSince2nd}",
            winner,
            raceMs,
            _msAtRaceWon,
            MsSinceSecondHealthy);
    }

    public void MarkPlayerOpened(string? channel, bool usedCached, bool raceWon, ILogger logger)
    {
        if (_playerOpenedLogged)
            return;
        _playerOpenedLogged = true;
        var sessionMs = _sinceSession.ElapsedMilliseconds;
        logger.LogInformation(
            "[FirstPlayDelay] player_open channel={Channel} usedCached={UsedCached} raceWon={RaceWon} " +
            "sessionMs={SessionMs} msSince1stHealthy={Ms1} msSince2ndHealthy={Ms2} " +
            "msSinceRaceStart={MsRace} msSinceRaceWon={MsWon} " +
            "(THIS is the TWO-HEALTHY stall duration when Ms2 is large)",
            channel,
            usedCached,
            raceWon,
            sessionMs,
            _msAtFirstHealthy is { } a ? sessionMs - a : null,
            _msAtSecondHealthy is { } b ? sessionMs - b : null,
            _msAtRaceStart is { } r ? sessionMs - r : null,
            _msAtRaceWon is { } w ? sessionMs - w : null);
    }
}
