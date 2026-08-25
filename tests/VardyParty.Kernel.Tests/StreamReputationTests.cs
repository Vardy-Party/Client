using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class StreamReputationTests
{
    [Theory]
    [InlineData(null, StreamReputation.None)]
    [InlineData("", StreamReputation.None)]
    [InlineData("   ", StreamReputation.None)]
    [InlineData("unknown-label", StreamReputation.None)]
    [InlineData("Very Good", StreamReputation.VeryGood)]
    [InlineData("very good", StreamReputation.VeryGood)]
    [InlineData("Good", StreamReputation.Good)]
    [InlineData("OK", StreamReputation.Ok)]
    [InlineData("ok", StreamReputation.Ok)]
    [InlineData("Poor", StreamReputation.Poor)]
    [InlineData("Bad", StreamReputation.Bad)]
    public void Parse_MapsApiLabelsCaseInsensitively(string? value, StreamReputation expected)
    {
        // Arrange
        var label = value;

        // Act
        var parsed = StreamReputationParser.Parse(label);

        // Assert
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void Rank_OrdersVeryGoodAboveGoodAboveOkAbovePoorAboveBadAboveNone()
    {
        // Arrange
        const StreamReputation veryGood = StreamReputation.VeryGood;
        const StreamReputation good = StreamReputation.Good;
        const StreamReputation ok = StreamReputation.Ok;
        const StreamReputation poor = StreamReputation.Poor;
        const StreamReputation bad = StreamReputation.Bad;
        const StreamReputation none = StreamReputation.None;

        // Act
        var ordered = new[] { none, bad, poor, ok, good, veryGood };

        // Assert
        Assert.True(veryGood > good);
        Assert.True(good > ok);
        Assert.True(ok > poor);
        Assert.True(poor > bad);
        Assert.True(bad > none);
        Assert.Equal(
            [StreamReputation.None, StreamReputation.Bad, StreamReputation.Poor, StreamReputation.Ok, StreamReputation.Good, StreamReputation.VeryGood],
            ordered);
    }
}
