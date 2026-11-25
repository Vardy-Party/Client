using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public sealed class V2NdjsonTests
{
    [Fact]
    public void ParseLine_ReadsChipEvent()
    {
        // Arrange
        var line = """{"type":"chip","label":"Chip A","url":"https://stream.example.test/a.m3u8"}""";

        // Act
        var ev = V2Ndjson.ParseLine(line);

        // Assert
        Assert.NotNull(ev);
        Assert.Equal("chip", ev.Type);
        Assert.Equal("Chip A", ev.Label);
        Assert.Equal("https://stream.example.test/a.m3u8", ev.Url);
    }

    [Fact]
    public async Task ReadLinesAsync_SkipsBlankLines()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"chips\"}\n\n{\"type\":\"done\"}\n");
        await using var stream = new MemoryStream(bytes);

        // Act
        var lines = new List<string>();
        await foreach (var line in V2Ndjson.ReadLinesAsync(stream, CancellationToken.None))
        {
            lines.Add(line);
        }

        // Assert
        Assert.Equal(2, lines.Count);
    }
}
