using System;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class MsixSignerPinTests
{
    [Fact]
    public void EnsureSameSigner_MatchingPublisherAndThumbprint_Succeeds()
    {
        MsixSignerPin.EnsureSameSigner(
            "CN=VardyParty",
            "288C22050831E54B0553CC6932F67FC809879A22",
            "CN=VardyParty",
            "288c22050831e54b0553cc6932f67fc809879a22");
    }

    [Fact]
    public void EnsureSameSigner_DifferentThumbprint_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MsixSignerPin.EnsureSameSigner(
                "CN=VardyParty",
                "288C22050831E54B0553CC6932F67FC809879A22",
                "CN=VardyParty",
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Contains("certificate", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureSameSigner_DifferentPublisher_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MsixSignerPin.EnsureSameSigner(
                "CN=VardyParty",
                "288C22050831E54B0553CC6932F67FC809879A22",
                "CN=SomeoneElse",
                "288C22050831E54B0553CC6932F67FC809879A22"));
        Assert.Contains("publisher", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CN=VardyParty", "CN=VardyParty", true)]
    [InlineData("CN=VardyParty", "CN = VardyParty", true)]
    [InlineData("CN=VardyParty", "CN=Other", false)]
    [InlineData("CN=VardyParty", null, false)]
    public void SamePublisher_NormalizesDistinguishedName(string? left, string? right, bool expected)
    {
        Assert.Equal(expected, MsixSignerPin.SamePublisher(left, right));
    }
}
