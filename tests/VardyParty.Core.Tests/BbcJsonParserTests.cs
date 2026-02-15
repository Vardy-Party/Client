using Microsoft.Extensions.Logging.Abstractions;
using VardyParty.Parsers;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class BbcJsonParserTests
    {
        private readonly BbcJsonParser _parser = new BbcJsonParser(NullLogger<BbcJsonParser>.Instance);

        [Fact]
        public void BuildEventStatusMapStreaming_ParsesInitialJson_Postponed()
        {
            var json = "{\"events\":[{\"id\":\"s-x\",\"status\":\"Postponed\",\"periodLabel\":{\"value\":\"Postponed\"}}]}";
            var html = new BbcHtmlBuilder().WithInitialJson(json).BuildPage();

            var map = _parser.BuildEventStatusMapStreaming(html);

            Assert.True(map.ContainsKey("s-x"));
            Assert.Equal("Postponed", map["s-x"].status);
            Assert.Equal("Postponed", map["s-x"].periodLabel);
        }

        [Fact]
        public void BuildEventStatusMapStreaming_MalformedJson_DoesNotThrow()
        {
            var malformed = "<script>window.__INITIAL_DATA__ = {\"events\":[{\"id\":\"s-1\",\"status\":\"Live\"}]"; // missing closing braces

            var ex = Record.Exception(() => _parser.BuildEventStatusMapStreaming(malformed));

            Assert.Null(ex); // parser should swallow JSON errors
            var result = _parser.BuildEventStatusMapStreaming(malformed);
            Assert.True(result.ContainsKey("s-1"));
        }
    }
}
