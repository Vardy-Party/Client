using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Playback;
using VardyParty.Streaming;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// The <c>[probe-timing]</c> log (docs/PROBE_SCHEDULING_RULES.md, "Probe timing log"): one
/// greppable prefix, <c>key=value</c> fields, ms since session start, hosts only.
/// </summary>
public class ProbeTimingLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Log_WritesPrefixEventAndMsSinceSessionStart_HostsOnly_QuotesSpaces()
    {
        // Arrange
        var clock = new Timeline.MilestoneClock(Start);
        var timing = new ProbeTimingLog();
        var logger = new CapturingLogger();
        timing.BeginSession(clock);
        using var tick = clock.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(1500), Timeout.InfiniteTimeSpan);
        clock.AdvanceToNext();

        // Act
        timing.Log(logger, "health-end",
            ("row", "lshls 1"),
            ("chip", null),
            ("cdn", ProbeTimingLog.Host("https://cdn.example.test/live/index.m3u8?token=secret")),
            ("result", StreamHealthStatus.Healthy),
            ("duration_ms", 1500L));

        // Assert
        var line = Assert.Single(logger.Lines);
        Assert.Equal("[probe-timing] event=health-end ms=1500 row=\"lshls 1\" cdn=cdn.example.test result=Healthy duration_ms=1500", line);
        Assert.Equal(LogLevel.Information, logger.Levels.Single());
        Assert.DoesNotContain("secret", line);
    }

    [Fact]
    public void SessionTrace_LogsPictureBackupSwitchAndSummary_WithTiers()
    {
        // Arrange
        var timing = new ProbeTimingLog();
        var logger = new CapturingLogger();
        var h1 = Row("H1");
        var m1 = Row("M1");
        var tiers = new StreamTiers([h1.Stream], [m1.Stream]);
        var pool = new StreamSwitchingService();
        pool.Initialize("Premier League", "Home United", "Away City");
        var trace = new ProbeSessionTrace(timing, logger, pool, () => tiers);
        pool.AddHealthyStream(h1);

        // Act
        trace.Picture(h1);
        var backupBeforeSecondRow = logger.Lines.Any(l => l.Contains("event=backup-ready", StringComparison.Ordinal));
        pool.AddHealthyStream(m1);
        trace.PoolChanged();
        pool.SwitchToNextStream();
        var countedBeforeVideo = logger.Lines.Any(l => l.Contains("event=switch ", StringComparison.Ordinal));
        pool.ConfirmPlaying(m1);
        trace.Dispose();

        // Assert
        Assert.False(backupBeforeSecondRow);
        Assert.False(countedBeforeVideo, "an index change is a request; the switch counts when video shows");
        Assert.Contains(logger.Lines, l => l.Contains("event=switch-requested ") && l.Contains("from_row=H1") && l.Contains("to_row=M1"));
        Assert.Contains(logger.Lines, l => l.StartsWith("[probe-timing] event=picture ", StringComparison.Ordinal) && l.Contains("row=H1 ") && l.Contains("tier=high"));
        Assert.Contains(logger.Lines, l => l.Contains("event=backup-ready ") && l.Contains("playing_tier=high") && l.Contains("backup_tier=medium") && l.Contains("backup_row=M1"));
        Assert.Contains(logger.Lines, l => l.Contains("event=switch ") && l.Contains("from_tier=high") && l.Contains("to_tier=medium") && l.Contains("switches=1"));
        Assert.Contains(logger.Lines, l => l.Contains("event=session-end ") && l.Contains("switches=1") && l.Contains("playing_tier=medium") && l.Contains("healthy=2"));
        Assert.DoesNotContain(logger.Lines, l => l.Contains("://", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionTrace_FailedSwitchThatStaysOnPlayingStream_IsNotCounted()
    {
        // Arrange - TV log 13 Nov 2026: Next onto Fubo US and NW IT, the fresh resolve
        // failed, playback stayed on VUS, yet switches=3 was logged.
        var timing = new ProbeTimingLog();
        var logger = new CapturingLogger();
        var vus = Row("VUS");
        var fubo = Row("Fubo US");
        var tiers = new StreamTiers([], []);
        var pool = new StreamSwitchingService();
        pool.Initialize("UEFA Nations League", "Moldova", "Kazakhstan");
        var trace = new ProbeSessionTrace(timing, logger, pool, () => tiers);
        pool.AddHealthyStream(vus);
        pool.AddHealthyStream(fubo);
        trace.Picture(vus);
        pool.ConfirmPlaying(vus);

        // Act - Next onto Fubo US, the switch fails, the index goes back to VUS.
        pool.SwitchToNextStream();
        pool.SwitchToStream(0);
        trace.Dispose();

        // Assert
        Assert.Equal(0, trace.Switches);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("event=switch ", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.Contains("event=switch-requested ") && l.Contains("to_row=\"Fubo US\""));
        Assert.Contains(logger.Lines, l => l.Contains("event=session-end ") && l.Contains("switches=0"));
    }

    private static EnrichedStream Row(string channel) => new()
    {
        Stream = new StreamModel { Url = $"https://{channel.ToLowerInvariant()}.example/match", Channel = channel },
        ResolvedM3U8Url = $"https://cdn.example/{channel.ToLowerInvariant()}/index.m3u8?token=t",
        Status = StreamResolutionStatus.Healthy,
    };

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Lines.Add(formatter(state, exception));
        }
    }
}
