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
