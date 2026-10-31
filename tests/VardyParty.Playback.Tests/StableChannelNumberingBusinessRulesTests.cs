using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Stable channel numbering business rules (docs/STREAM_PLAYBACK_RULES.md, "Stable
/// channel numbering"). Every client (Android, Windows, Linux, Apple) numbers channels
/// from the shared <see cref="StreamSwitchingService"/> pool, so these hold everywhere:
/// a stream's number is fixed while the user cycles (Next, Previous, wraparound, chips),
/// a new healthy stream is appended with the next number, a drop closes the gap keeping
/// relative order, and a merged duplicate never takes a number. Recommendations may pick
/// a stream to jump to but never reorder the numbered list (the 20:18:10 UK renumbering
/// on 31 Oct 2026 Jon disliked came from a wraparound re-sort).
/// </summary>
public class StableChannelNumberingBusinessRulesTests
{
    private const string GoalStream = "https://edge.wedd48c.top/premium109/index.m3u8?t=1793478144.aaa";
    private const string GoalStreamOtherToken = "https://edge.wedd48c.top/premium109/index.m3u8?t=1793478999.bbb";
    private const string FourK = "https://edge.wedd48c.top/premium155/index.m3u8?t=1793478144.ccc";
    private const string Besteverstr = "https://dur123.durrfity.shop/simpch12/index.m3u8?t=1";
    private const string SoccerStreams = "https://edgestream2.pro/hls/syaLqucEne4k.m3u8";
    private const string Late = "https://edge2.matchix.org/live/late/index.m3u8";

    private static EnrichedStream Healthy(string channel, string m3u8) => new()
    {
        Stream = new StreamModel
        {
            Url = $"https://{channel.ToLowerInvariant()}.example/ac-milan-vs-inter",
            Channel = channel,
        },
        ResolvedM3U8Url = m3u8,
        Status = StreamResolutionStatus.Healthy,
    };

    private static StreamSwitchingService Pool(params (string Channel, string M3u8)[] rows)
    {
        var pool = new StreamSwitchingService();
        pool.Initialize("Serie A", "AC Milan", "Inter");
        foreach (var (channel, m3u8) in rows)
            pool.AddHealthyStream(Healthy(channel, m3u8));
        return pool;
    }

    private static StreamSwitchingService MilanInter() => Pool(
        ("Goal stream", GoalStream),
        ("Besteverstr", Besteverstr),
        ("4Kstream", FourK),
        ("SoccerStreams", SoccerStreams));

    /// <summary>Channel number (1-based) per visible row, as the overlay shows it.</summary>
    private static Dictionary<string, int> Numbers(StreamSwitchingService pool) =>
        pool.GetHealthyStreams()
            .Select((s, i) => (s.Stream.Channel!, Number: i + 1))
            .ToDictionary(x => x.Item1, x => x.Number);

    private static string? Current(StreamSwitchingService pool) => pool.GetCurrentStream()?.Stream.Channel;

    [Fact]
    public void Rule1_CyclingNextAndPrevious_NeverChangesNumbers()
    {
        var pool = MilanInter();
        var before = Numbers(pool);

        for (var i = 0; i < 9; i++)
            Assert.True(pool.SwitchToNextStream());
        for (var i = 0; i < 5; i++)
            Assert.True(pool.SwitchToPreviousStream());

        Assert.Equal(before, Numbers(pool));
    }

    [Fact]
    public void Rule1_NextWalksChannelsInNumberOrder()
    {
        var pool = MilanInter();
        var seen = new List<string?> { Current(pool) };
        for (var i = 0; i < 3; i++)
        {
            pool.SwitchToNextStream();
            seen.Add(Current(pool));
        }

        Assert.Equal(["Goal stream", "Besteverstr", "4Kstream", "SoccerStreams"], seen);
    }

    [Fact]
    public void Rule2_Wraparound_NextOnLastChannelGoesToChannelOne_AndKeepsNumbers()
    {
        var pool = MilanInter();
        var before = Numbers(pool);
        pool.SwitchToStream(3);
        Assert.True(pool.WouldWrapOnNext());

        Assert.True(pool.SwitchToNextStream());

        Assert.Equal("Goal stream", Current(pool));
        Assert.Equal(1, pool.GetCurrentStreamIndex());
        Assert.Equal(before, Numbers(pool));
    }

    [Fact]
    public void Rule2_PreviousOnChannelOneGoesToLastChannel()
    {
        var pool = MilanInter();

        Assert.True(pool.SwitchToPreviousStream());

        Assert.Equal("SoccerStreams", Current(pool));
        Assert.Equal(4, pool.GetCurrentStreamIndex());
    }

    [Fact]
    public void Rule3_RecommendationJump_SelectsWithoutReordering()
    {
        // A recommendation / chip picks 4Kstream: playback jumps there, numbers stay.
        var pool = MilanInter();
        var before = Numbers(pool);
        var fourK = pool.GetHealthyStreams()[2];

        Assert.True(pool.SwitchToMatchingStream(fourK.Stream.Url, null));

        Assert.Equal("4Kstream", Current(pool));
        Assert.Equal(3, pool.GetCurrentStreamIndex());
        Assert.Equal(before, Numbers(pool));

        // Next after the jump continues from channel 3, not from a re-sorted list.
        pool.SwitchToNextStream();
        Assert.Equal("SoccerStreams", Current(pool));
    }

    [Fact]
    public void Rule3_PoolHasNoReorderOperation()
    {
        // The re-sort API is gone so no caller (wraparound, recommendations) can renumber.
        Assert.Null(typeof(StreamSwitchingService).GetMethod("ReorderHealthyStreams"));
        Assert.Null(typeof(VardyParty.Ports.IStreamSwitchingService).GetMethod("ReorderHealthyStreams"));
    }

    [Fact]
    public void Rule4_NewHealthyStream_AppendsWithNextNumber_WithoutMovingCurrent()
    {
        var pool = MilanInter();
        pool.SwitchToStream(1);
        var before = Numbers(pool);

        pool.AddHealthyStream(Healthy("Late", Late));

        var after = Numbers(pool);
        Assert.Equal(5, after["Late"]);
        foreach (var (channel, number) in before)
            Assert.Equal(number, after[channel]);
        Assert.Equal("Besteverstr", Current(pool));
        Assert.Equal(2, pool.GetCurrentStreamIndex());
    }

    [Fact]
    public void Rule5_Drop_ClosesTheGap_KeepingRelativeOrder()
    {
        var pool = MilanInter();
        pool.SwitchToStream(1); // Besteverstr (2)

        Assert.True(pool.RemoveCurrentStream());

        Assert.Equal(
            new Dictionary<string, int> { ["Goal stream"] = 1, ["4Kstream"] = 2, ["SoccerStreams"] = 3 },
            Numbers(pool));
        // The row that took the dropped number is now current.
        Assert.Equal("4Kstream", Current(pool));
        Assert.Equal(2, pool.GetCurrentStreamIndex());
    }

    [Fact]
    public void Rule5_DropOfLastChannel_LandsOnNewLast()
    {
        var pool = MilanInter();
        pool.SwitchToStream(3);

        pool.RemoveCurrentStream();

        Assert.Equal(3, pool.GetHealthyStreams().Count);
        Assert.Equal("4Kstream", Current(pool));
        Assert.Equal(3, pool.GetCurrentStreamIndex());
    }

    [Fact]
    public void Rule6_MergedDuplicate_NeverTakesANumber()
    {
        var pool = MilanInter();
        var before = Numbers(pool);

        pool.AddHealthyStream(Healthy("Bizzstreams", GoalStreamOtherToken));
        pool.AddHealthyStream(Healthy("Late", Late));

        var after = Numbers(pool);
        Assert.False(after.ContainsKey("Bizzstreams"));
        Assert.Equal(5, after["Late"]);
        foreach (var (channel, number) in before)
            Assert.Equal(number, after[channel]);
    }

    [Fact]
    public void Rule6_FallbackPromotedOnFailure_KeepsTheSameNumber()
    {
        var pool = MilanInter();
        pool.AddHealthyStream(Healthy("Bizzstreams", GoalStreamOtherToken));

        // Goal stream (1) fails; its merged duplicate takes over channel 1 in place.
        Assert.True(pool.RemoveCurrentStream());

        Assert.Equal(1, Numbers(pool)["Bizzstreams"]);
        Assert.Equal(2, Numbers(pool)["Besteverstr"]);
        Assert.Equal(4, pool.GetHealthyStreams().Count);
    }
}
