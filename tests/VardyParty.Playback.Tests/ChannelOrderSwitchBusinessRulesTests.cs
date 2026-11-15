using System.Collections.Generic;
using System.Linq;
using VardyParty.Kernel;
using VardyParty.Playback;
using Xunit;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Next, Previous, auto-advance and GetUpcoming walk channel numbers in order (with
/// wrap), skipping only rows tried or failed this cycle (docs/PROBE_SCHEDULING_RULES.md
/// rule 11; docs/STREAM_PLAYBACK_RULES.md). Tiers do not change switch order: they are
/// for LocalService slot scheduling only. The prewarmer follows the same order
/// (<see cref="StreamSwitchingService.GetUpcoming"/>).
/// </summary>
public class ChannelOrderSwitchBusinessRulesTests
{
    private static EnrichedStream Row(string channel) => new()
    {
        Stream = new StreamModel { Url = $"https://{channel.ToLowerInvariant()}.example/match", Channel = channel },
        ResolvedM3U8Url = $"https://cdn.example/{channel.ToLowerInvariant()}/index.m3u8",
        Status = StreamResolutionStatus.Healthy,
    };

    private static StreamSwitchingService Pool(params EnrichedStream[] rows)
    {
        var pool = new StreamSwitchingService();
        pool.Initialize("Fictional League", "Home United", "Away City");
        foreach (var row in rows)
            pool.AddHealthyStream(row);
        return pool;
    }

    private static List<string> Channels(StreamSwitchingService pool) =>
        pool.GetHealthyStreams().Select(s => s.Stream.Channel!).ToList();

    [Fact]
    public void Rule_NextFromTheStartRow_GoesToTheNextChannel()
    {
        // Arrange
        var first = Row("Channel One");
        var second = Row("Channel Two");
        var third = Row("Channel Three");
        var pool = Pool(first, second, third);

        // Act
        var upcoming = pool.GetUpcoming(1);
        var switched = pool.SwitchToNextStream();

        // Assert — Next lands on #2, and the channel numbers are unchanged.
        Assert.Same(second, upcoming.Single());
        Assert.True(switched);
        Assert.Same(second, pool.GetCurrentStream());
        Assert.Equal(["Channel One", "Channel Two", "Channel Three"], Channels(pool));
    }

    [Fact]
    public void Rule_ALaterRowJoining_DoesNotReplaceTheNextChannel()
    {
        // Arrange
        var first = Row("Channel One");
        var second = Row("Channel Two");
        var late = Row("Channel Late");
        var pool = Pool(first, second);
        var before = pool.GetNextHealthyStream();

        // Act
        pool.AddHealthyStream(late);
        var after = pool.GetNextHealthyStream();

        // Assert — auto-advance and GetUpcoming stay on #2 after a row joins as #3.
        Assert.Same(second, before);
        Assert.Same(second, after);
        Assert.Same(second, pool.GetUpcoming(1).Single());
        Assert.Equal(["Channel One", "Channel Two", "Channel Late"], Channels(pool));
    }

    [Fact]
    public void Rule_NextWalksChannelNumbersAndWraps()
    {
        // Arrange
        var first = Row("Channel One");
        var second = Row("Channel Two");
        var third = Row("Channel Three");
        var pool = Pool(first, second, third);

        // Act
        pool.SwitchToNextStream();
        var afterFirst = pool.GetCurrentStream();
        pool.SwitchToNextStream();
        var afterSecond = pool.GetCurrentStream();
        pool.SwitchToNextStream();

        // Assert — #1 -> #2 -> #3 -> wrap to #1.
        Assert.Same(second, afterFirst);
        Assert.Same(third, afterSecond);
        Assert.Same(first, pool.GetCurrentStream());
    }

    [Fact]
    public void Rule_ASkippedNextChannel_IsPassedOver()
    {
        // Arrange
        var first = Row("Channel One");
        var second = Row("Channel Two");
        var third = Row("Channel Three");
        var fourth = Row("Channel Four");
        var pool = Pool(first, second, third, fourth);
        pool.MarkTriedInCycle(second, null, "No video (audio only)");

        // Act
        pool.SwitchToNextStream();

        // Assert — #2 was tried this cycle, so Next goes to #3.
        Assert.Same(third, pool.GetCurrentStream());
    }

    [Fact]
    public void Rule_GetUpcoming_SkipsARowTriedThisCycle_LikeNext()
    {
        // Arrange
        var first = Row("Channel One");
        var second = Row("Channel Two");
        var third = Row("Channel Three");
        var fourth = Row("Channel Four");
        var pool = Pool(first, second, third, fourth);
        pool.MarkTriedInCycle(second, null, "No video (audio only)");

        // Act
        var upcoming = pool.GetUpcoming(2).ToList();
        var next = pool.GetNextHealthyStream();

        // Assert — the prewarmer never warms a row Next will pass over.
        Assert.Equal([third, fourth], upcoming);
        Assert.Same(next, upcoming[0]);
        Assert.DoesNotContain(second, upcoming);
    }

    [Fact]
    public void Rule_WouldWrapOnNext_FollowsChannelOrder()
    {
        // Arrange — from #2 Next is #3 (no wrap); from #3 Next wraps to #1.
        var first = Row("Channel One");
        var second = Row("Channel Two");
        var third = Row("Channel Three");
        var pool = Pool(first, second, third);
        pool.SwitchToStream(1);

        // Act
        var wrapsFromSecond = pool.WouldWrapOnNext();
        var nextFromSecond = pool.GetNextHealthyStream();
        var movedBack = pool.SwitchToPreviousStream();
        var afterPrevious = pool.GetCurrentStream();
        pool.SwitchToStream(2);
        var wrapsFromThird = pool.WouldWrapOnNext();
        var nextFromThird = pool.GetNextHealthyStream();

        // Assert
        Assert.False(wrapsFromSecond);
        Assert.Same(third, nextFromSecond);
        Assert.True(movedBack);
        Assert.Same(first, afterPrevious);
        Assert.True(wrapsFromThird);
        Assert.Same(first, nextFromThird);
    }

    [Fact]
    public void Rule_GetUpcoming_MatchesWhereNextGoes()
    {
        // Arrange
        var first = Row("Channel One");
        var second = Row("Channel Two");
        var third = Row("Channel Three");
        var pool = Pool(first, second, third);

        // Act
        var fromStart = pool.GetUpcoming(2).ToList();
        pool.SwitchToNextStream();
        var fromSecond = pool.GetUpcoming(2).ToList();

        // Assert
        Assert.Equal([second, third], fromStart);
        Assert.Equal([third, first], fromSecond);
    }
}
