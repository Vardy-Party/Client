using Xunit;

namespace VardyParty.Hosting.Tests;

public class IceCandidateKindTests
{
    [Fact]
    public void Of_ReadsHostCandidate()
    {
        const string candidate = "candidate:1 1 udp 2130706431 192.0.2.10 54321 typ host generation 0";

        Assert.Equal("host", IceCandidateKind.Of(candidate));
    }

    [Fact]
    public void Of_ReadsServerReflexiveCandidate()
    {
        const string candidate = "candidate:2 1 udp 1694498815 198.51.100.7 60000 typ srflx raddr 192.0.2.10 rport 54321 generation 0";

        Assert.Equal("srflx", IceCandidateKind.Of(candidate));
    }

    [Fact]
    public void Of_ReadsRelayCandidateWithSdpAttributePrefix()
    {
        const string candidate = "a=candidate:3 1 udp 16777215 203.0.113.5 3478 typ relay raddr 198.51.100.7 rport 60000";

        Assert.Equal("relay", IceCandidateKind.Of(candidate));
    }

    [Fact]
    public void Of_IgnoresCaseAndReadsPeerReflexiveCandidate()
    {
        const string candidate = "4 1 UDP 1862270975 192.0.2.11 50000 TYP PRFLX";

        Assert.Equal("prflx", IceCandidateKind.Of(candidate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("candidate:5 1 udp 1 192.0.2.12 1 typ")]
    [InlineData("candidate:6 1 udp 1 192.0.2.13 1 typ something")]
    public void Of_ReturnsUnknownWhenTheKindIsMissing(string? candidate)
    {
        Assert.Equal("unknown", IceCandidateKind.Of(candidate));
    }
}
