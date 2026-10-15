using System;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Hosting;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class RemoteComputeDirectDropTests
{
    [Fact]
    public void Close_BeforeReady_SignalsFailedAndDoesNotFault()
    {
        // Arrange
        const string correlationId = "abc12345";

        // Act
        var decision = DirectChannelClose.Decide(
            readyAnnounced: false,
            useDirect: false,
            resultSeen: false,
            correlationId);

        // Assert
        Assert.True(decision.SignalFailed);
        Assert.Null(decision.Fault);
    }

    [Fact]
    public void Close_AfterReadyBeforeDirect_SignalsFailedAndDoesNotFault()
    {
        // Arrange
        const string correlationId = "abc12345";

        // Act
        var decision = DirectChannelClose.Decide(
            readyAnnounced: true,
            useDirect: false,
            resultSeen: false,
            correlationId);

        // Assert
        Assert.True(decision.SignalFailed);
        Assert.Null(decision.Fault);
    }

    [Fact]
    public void Close_WhileDirectBeforeResult_NamesThisPhoneAndTheCorrelationId()
    {
        // Arrange
        const string correlationId = "abc12345";

        // Act
        var decision = DirectChannelClose.Decide(
            readyAnnounced: true,
            useDirect: true,
            resultSeen: false,
            correlationId);

        // Assert
        Assert.False(decision.SignalFailed);
        Assert.Equal(
            "This phone failed: direct connection dropped. Correlation id: abc12345",
            decision.Fault?.Display);
    }

    [Fact]
    public void RelayPingInterval_StaysInsideAMinute()
    {
        // Arrange
        // Act
        var interval = RemoteComputePlayClient.RelayPingInterval;

        // Assert
        Assert.True(interval > TimeSpan.Zero);
        Assert.True(interval < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Close_WhileDirectAfterRpcPart_HarvestWaitNamesThisPhoneAndTheCorrelationId()
    {
        // Arrange
        const string correlationId = "abc12345";
        var harvest = new MpHarvestSession();
        var chips = MpNdjson.ParseLine("""{"type":"chips","streams":["Chip A"]}""");
        harvest.Apply(chips!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var waiting = harvest.WaitForAsync(null, timeout.Token);

        // Act
        var resultSeen = DirectChannelClose.MarksResultSeen(chips);
        var decision = DirectChannelClose.Decide(
            readyAnnounced: true,
            useDirect: true,
            resultSeen,
            correlationId);
        harvest.Complete(failed: true);
        var first = await waiting;
        var fault = DirectChannelClose.EmptyHarvestFault(
            directDropped: decision.Fault is not null,
            egressProblem: null,
            harvestError: harvest.Error,
            correlationId);

        // Assert
        Assert.False(resultSeen);
        Assert.Null(first);
        Assert.Equal(
            "This phone failed: direct connection dropped. Correlation id: abc12345",
            fault.Display);
    }

    [Fact]
    public void MarksResultSeen_ChipWithUrl_IsTrue()
    {
        // Arrange
        var chip = new MpPlayEvent
        {
            Type = "chip",
            Label = "Chip A",
            Url = "https://stream.example.test/a.m3u8"
        };

        // Act
        var seen = DirectChannelClose.MarksResultSeen(chip);

        // Assert
        Assert.True(seen);
    }

    [Fact]
    public void Close_AfterResult_DoesNotFault()
    {
        // Arrange
        const string correlationId = "abc12345";

        // Act
        var decision = DirectChannelClose.Decide(
            readyAnnounced: true,
            useDirect: true,
            resultSeen: true,
            correlationId);

        // Assert
        Assert.False(decision.SignalFailed);
        Assert.Null(decision.Fault);
    }
}
