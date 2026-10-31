using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Proven-mirror dedup business rules (docs/STREAM_PLAYBACK_RULES.md "Proven mirrors").
/// Two healthy rows with different m3u8 identities are merged only when the health check's
/// segment clocks prove they are the same encoder output. Host names (edgestream2 vs
/// edgestream5) are never a reason; missing evidence never merges; different premiumNNN
/// channels never merge. A proven mirror is a hidden fallback with no channel number, and
/// the existing identity-key dedup and stable numbering are unchanged. Numbers are from the
/// 31 Oct 2026 Aston Villa v Fulham v223 session (UTC).
/// </summary>
public class ProvenMirrorDedupBusinessRulesTests
{
    private const string Edge2 = "https://edgestream2.pro/hls/syaLqucEne4k.m3u8";
    private const string Edge5 = "https://edgestream5.pro/hls/syaLqucEne4k.m3u8";
    private const string Lshls = "https://hls.lshls.st/secure_hls.php?path=/hls/villafulham/index.m3u8";

    private static readonly DateTimeOffset At2021 = new(2026, 10, 31, 20, 21, 45, 362, TimeSpan.Zero);
    private static readonly DateTimeOffset At2023 = new(2026, 10, 31, 20, 23, 31, 2, TimeSpan.Zero);
    private static readonly DateTimeOffset At2024 = new(2026, 10, 31, 20, 24, 17, 960, TimeSpan.Zero);

    private static string PtsPlaylist(string stem, long firstPts)
    {
        var lines = new List<string> { "#EXTM3U", "#EXT-X-TARGETDURATION:6", "#EXT-X-MEDIA-SEQUENCE:157384" };
        for (var i = 0; i < 3; i++)
        {
            lines.Add("#EXTINF:" + 6.0.ToString("0.000", CultureInfo.InvariantCulture) + ",");
            lines.Add($"{stem}-{firstPts + i * 540_000}.ts?st=tok{i}");
        }

        return string.Join("\n", lines);
    }

    private static EnrichedStream Row(string channel, string m3u8, MirrorEvidence? evidence,
        StreamReputation reputation = StreamReputation.None) => new()
        {
            Stream = new StreamModel
            {
                Url = $"https://{channel.ToLowerInvariant()}.example/aston-villa-vs-fulham",
                Channel = channel,
                Reputation = reputation,
            },
            ResolvedM3U8Url = m3u8 + "?st=" + channel + "&e=1793488895",
            Status = StreamResolutionStatus.Healthy,
            Health = new StreamHealth { Status = StreamHealthStatus.Healthy, Url = m3u8, MirrorEvidence = evidence },
        };

    private static MirrorEvidence Pts(string url, string stem, long firstPts, DateTimeOffset at) =>
        MirrorEvidence.FromMediaPlaylist(PtsPlaylist(stem, firstPts), url, url, at)!;

    private static EnrichedStream Soccerstreams() =>
        Row("Soccerstreams", Edge2, Pts(Edge2, "syaLqucEne4k", 944307180, At2021));

    private static EnrichedStream Embedcenter() =>
        Row("Embedcenter", Edge5, Pts(Edge5, "syaLqucEne4k", 957960270, At2024));

    private static (StreamSwitchingService Pool, ListLogger Log) NewPool()
    {
        var log = new ListLogger();
        var pool = new StreamSwitchingService(log);
        pool.Initialize("Premier League", "Aston Villa", "Fulham");
        return (pool, log);
    }

    private static List<string> Visible(StreamSwitchingService pool) =>
        pool.GetHealthyStreams().Select(s => s.Stream.Channel!).ToList();

    [Fact]
    public void Rule1_Edgestream2And5WithMatchingSegmentClock_ShowOnce_MirrorIsHiddenFallback()
    {
        var (pool, log) = NewPool();
        var first = Soccerstreams();
        var mirror = Embedcenter();

        pool.AddHealthyStream(first);
        pool.AddHealthyStream(mirror);

        Assert.Equal(new[] { "Soccerstreams" }, Visible(pool));
        Assert.Same(mirror, Assert.Single(pool.GetMergedFallbacks(first)));
        var line = Assert.Single(log.Lines, l => l.StartsWith("[Dedup] proven mirror:"));
        Assert.Contains("Soccerstreams https://edgestream2.pro/hls/syaLqucEne4k.m3u8", line);
        Assert.Contains("Embedcenter https://edgestream5.pro/hls/syaLqucEne4k.m3u8", line);
        Assert.Contains("pts delta", line);
    }

    [Fact]
    public void Rule2_ProvenMirrorTakesNoNumber_LaterStreamsKeepTheNextNumber()
    {
        var (pool, _) = NewPool();
        pool.AddHealthyStream(Soccerstreams());
        pool.AddHealthyStream(Row("Lshls", Lshls, evidence: null));
        pool.AddHealthyStream(Embedcenter());
        pool.AddHealthyStream(Row("Goal stream", "https://edge.wedd48c.top/premium109/index.m3u8", null));

        Assert.Equal(new[] { "Soccerstreams", "Lshls", "Goal stream" }, Visible(pool));
    }

    [Fact]
    public void Rule3_WhenTheVisibleEntryFails_TheProvenMirrorIsPromotedInPlace()
    {
        var (pool, _) = NewPool();
        pool.AddHealthyStream(Soccerstreams());
        pool.AddHealthyStream(Embedcenter());
        pool.AddHealthyStream(Row("Lshls", Lshls, evidence: null));

        Assert.True(pool.RemoveCurrentStream());

        Assert.Equal(new[] { "Embedcenter", "Lshls" }, Visible(pool));
        Assert.Equal(1, pool.GetCurrentStreamIndex());
    }

    [Fact]
    public void Rule4_HostSimilarityIsNeverAReason_ClocksTenSecondsApartStaySeparate()
    {
        var (pool, log) = NewPool();
        pool.AddHealthyStream(Soccerstreams());
        pool.AddHealthyStream(Row("Embedcenter", Edge5, Pts(Edge5, "syaLqucEne4k", 957960270 + 900_000, At2024)));

        Assert.Equal(new[] { "Soccerstreams", "Embedcenter" }, Visible(pool));
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("[Dedup] proven mirror:"));
    }

    [Fact]
    public void Rule5_MissingEvidenceNeverMerges_SiblingHostWithoutEvidenceStaysSeparate()
    {
        var (pool, _) = NewPool();
        pool.AddHealthyStream(Soccerstreams());
        pool.AddHealthyStream(Row("Embedcenter", Edge5, evidence: null));

        Assert.Equal(new[] { "Soccerstreams", "Embedcenter" }, Visible(pool));
    }

    [Fact]
    public void Rule6_DifferentPremiumChannelsNeverMerge_EvenWithIdenticalClocks()
    {
        var (pool, _) = NewPool();
        foreach (var id in new[] { "109", "155", "261" })
        {
            var url = $"https://edge.wedd48c.top/premium{id}/index.m3u8";
            pool.AddHealthyStream(Row($"premium{id}", url, Pts(url, "seg", 944307180, At2021)));
        }

        Assert.Equal(new[] { "premium109", "premium155", "premium261" }, Visible(pool));
    }

    [Fact]
    public void Rule7_UnrelatedStreamsStaySeparate()
    {
        var (pool, _) = NewPool();
        pool.AddHealthyStream(Soccerstreams());
        const string northgate = "https://edgestream7.pro/hls/northgate.m3u8";
        pool.AddHealthyStream(Row("Northgate", northgate, Pts(northgate, "northgate", 957960270, At2024)));

        Assert.Equal(new[] { "Soccerstreams", "Northgate" }, Visible(pool));
    }

    [Fact]
    public void Rule8_LaterRowOnTheMirrorUrl_MergesWithoutNewEvidence()
    {
        var (pool, _) = NewPool();
        var first = Soccerstreams();
        pool.AddHealthyStream(first);
        pool.AddHealthyStream(Embedcenter());
        pool.AddHealthyStream(Row("Pitchstream", Edge5, evidence: null));

        Assert.Equal(new[] { "Soccerstreams" }, Visible(pool));
        Assert.Equal(2, pool.GetMergedFallbacks(first).Count);
    }

    [Fact]
    public void Rule9_SameIdentityKeyDedupIsUnchanged()
    {
        var (pool, log) = NewPool();
        var first = Soccerstreams();
        pool.AddHealthyStream(first);
        pool.AddHealthyStream(Row("1ststreams", Edge2, Pts(Edge2, "syaLqucEne4k", 953640450, At2023)));

        Assert.Equal(new[] { "Soccerstreams" }, Visible(pool));
        Assert.Single(pool.GetMergedFallbacks(first));
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("[Dedup] proven mirror:"));
    }

    [Fact]
    public void Rule10_PlayingEntryIsNeverSwappedForAHigherReputationMirror()
    {
        var (pool, _) = NewPool();
        var playing = Soccerstreams();
        pool.AddHealthyStream(playing);
        pool.AddHealthyStream(Row("Embedcenter", Edge5, Pts(Edge5, "syaLqucEne4k", 957960270, At2024),
            StreamReputation.VeryGood));

        Assert.Same(playing, pool.GetCurrentStream());
        Assert.Equal(new[] { "Soccerstreams" }, Visible(pool));
    }

    private sealed class ListLogger : ILogger<StreamSwitchingService>
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}
