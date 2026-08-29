using System.Text.Json;
using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class StreamReputationJsonConverterTests
{
    // Mirrors HttpContentJsonExtensions.ReadFromJsonAsync, which ApiService uses.
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("\"Very good\"", StreamReputation.VeryGood)]
    [InlineData("\"Very Good\"", StreamReputation.VeryGood)]
    [InlineData("\"very good\"", StreamReputation.VeryGood)]
    [InlineData("\" Very Good \"", StreamReputation.VeryGood)]
    [InlineData("\"Good\"", StreamReputation.Good)]
    [InlineData("\"OK\"", StreamReputation.Ok)]
    [InlineData("\"Poor\"", StreamReputation.Poor)]
    [InlineData("\"Bad\"", StreamReputation.Bad)]
    [InlineData("\"\"", StreamReputation.None)]
    [InlineData("\"   \"", StreamReputation.None)]
    [InlineData("null", StreamReputation.None)]
    [InlineData("\"Garbage\"", StreamReputation.None)]
    public void Deserialize_StreamPayload_MapsReputationWithoutThrowing(string reputationJson, StreamReputation expected)
    {
        // Arrange
        var json = $$"""{"href":"https://streams.example.com/match","streams":[{"url":"https://streams.example.com/1","channel":"Channel North","reputation":{{reputationJson}}}]}""";

        // Act
        var response = JsonSerializer.Deserialize<StreamResponse>(json, WebOptions);

        // Assert
        Assert.NotNull(response);
        var stream = Assert.Single(response!.Streams);
        Assert.Equal(expected, stream.Reputation);
    }

    [Theory]
    [InlineData("5", StreamReputation.VeryGood)]
    [InlineData("0", StreamReputation.None)]
    [InlineData("99", StreamReputation.None)]
    [InlineData("true", StreamReputation.None)]
    [InlineData("{\"nested\":\"value\"}", StreamReputation.None)]
    [InlineData("[\"list\"]", StreamReputation.None)]
    public void Deserialize_UnexpectedTokenShapes_NeverThrows(string reputationJson, StreamReputation expected)
    {
        // Arrange
        var json = $$"""{"reputation":{{reputationJson}},"channel":"Channel North"}""";

        // Act
        var stream = JsonSerializer.Deserialize<Stream>(json, WebOptions);

        // Assert
        Assert.NotNull(stream);
        Assert.Equal(expected, stream!.Reputation);
        Assert.Equal("Channel North", stream.Channel);
    }

    [Theory]
    [InlineData(StreamReputation.None, "\"\"")]
    [InlineData(StreamReputation.Bad, "\"Bad\"")]
    [InlineData(StreamReputation.Poor, "\"Poor\"")]
    [InlineData(StreamReputation.Ok, "\"OK\"")]
    [InlineData(StreamReputation.Good, "\"Good\"")]
    [InlineData(StreamReputation.VeryGood, "\"Very Good\"")]
    public void Serialize_EmitsCanonicalWireLabel(StreamReputation value, string expectedJson)
    {
        // Arrange
        var reputation = value;

        // Act
        var json = JsonSerializer.Serialize(reputation, WebOptions);

        // Assert
        Assert.Equal(expectedJson, json);
    }
}
