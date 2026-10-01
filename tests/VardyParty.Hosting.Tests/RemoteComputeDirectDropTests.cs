using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class RemoteComputeDirectDropTests
{
    [Fact]
    public void DirectConnectionDrop_NamesThisPhoneAndTheCorrelationId()
    {
        var fault = new RemoteComputeFault(
            "This phone",
            RemoteComputePlayClient.DirectConnectionDroppedMessage,
            "abc12345");

        Assert.Equal(
            "This phone failed: direct connection dropped. Correlation id: abc12345",
            fault.Display);
    }
}
