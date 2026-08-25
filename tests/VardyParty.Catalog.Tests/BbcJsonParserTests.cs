using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using VardyParty.Catalog;

namespace VardyParty.Catalog.Tests
{
    public class BbcJsonParserTests
    {
        private readonly BbcJsonParser _parser = new BbcJsonParser(NullLogger<BbcJsonParser>.Instance);

        [Fact]
        public void BuildEventStatusMapStreaming_ParsesInitialJson_Postponed()
        {
            // Arrange
            var json = "{\"events\":[{\"id\":\"s-x\",\"status\":\"Postponed\",\"periodLabel\":{\"value\":\"Postponed\"}}]}";
            var html = new BbcHtmlBuilder().WithInitialJson(json).BuildPage();

            // Act
            var map = _parser.BuildEventStatusMapStreaming(html);

            // Assert
            Assert.True(map.ContainsKey("s-x"));
            Assert.Equal("Postponed", map["s-x"].status);
            Assert.Equal("Postponed", map["s-x"].periodLabel);
        }

        [Fact]
        public void BuildEventStatusMapStreaming_ParsesEscapedInitialData_LikeBbc()
        {
            // Arrange
            // BBC serves __INITIAL_DATA__ as an escaped JSON string assignment.
            // Literal page text resembles: __INITIAL_DATA__="{\"id\":\"s-...\",\"status\":\"MidEvent\",...}"
            var html =
                "__INITIAL_DATA__=\"{" +
                "\\\"id\\\":\\\"s-escaped1\\\"," +
                "\\\"periodLabel\\\":{\\\"value\\\":\\\"87'\\\"}," +
                "\\\"status\\\":\\\"MidEvent\\\"," +
                "\\\"statusComment\\\":{\\\"value\\\":\\\"87 minutes\\\"}" +
                "}\"";

            // Act
            var map = _parser.BuildEventStatusMapStreaming(html);

            // Assert
            Assert.True(map.ContainsKey("s-escaped1"));
            Assert.Equal("MidEvent", map["s-escaped1"].status);
            Assert.Equal("87'", map["s-escaped1"].periodLabel);
            Assert.Equal("87 minutes", map["s-escaped1"].statusComment);
        }

        [Fact]
        public void BuildEventStatusMapStreaming_MalformedJson_DoesNotThrow()
        {
            // Arrange
            var malformed = "<script>window.__INITIAL_DATA__ = {\"events\":[{\"id\":\"s-1\",\"status\":\"Live\"}]"; // missing closing braces

            // Act
            var ex = Record.Exception(() => _parser.BuildEventStatusMapStreaming(malformed));
            var result = _parser.BuildEventStatusMapStreaming(malformed);

            // Assert
            Assert.Null(ex); // parser should swallow JSON errors
            Assert.True(result.ContainsKey("s-1"));
        }
    }
}
