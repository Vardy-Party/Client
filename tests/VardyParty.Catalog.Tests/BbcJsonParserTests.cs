using System;
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

        private static string EscapedFixtureHtml(string id, string startDateTime) =>
            "__INITIAL_DATA__=\"{" +
            $"\\\"id\\\":\\\"{id}\\\"," +
            $"\\\"startDateTime\\\":\\\"{startDateTime}\\\"," +
            "\\\"status\\\":\\\"PreEvent\\\"" +
            "}\"";

        [Fact]
        public void BuildEventMapsStreaming_OffsetlessAugustKickoff_IsLondonBst()
        {
            // Arrange: no timezone in the string — London wall clock; August is BST (UTC+1).
            var html = EscapedFixtureHtml("s-aug1", "2026-08-15T15:00:00");

            // Act
            var (_, kickoffs) = _parser.BuildEventMapsStreaming(html);

            // Assert
            Assert.Equal(new DateTime(2026, 8, 15, 14, 0, 0, DateTimeKind.Utc), kickoffs["s-aug1"]);
            Assert.Equal(DateTimeKind.Utc, kickoffs["s-aug1"].Kind);
        }

        [Fact]
        public void BuildEventMapsStreaming_OffsetlessJanuaryKickoff_IsLondonGmt()
        {
            // Arrange: January is GMT (UTC+0) — wall clock equals the UTC instant.
            var html = EscapedFixtureHtml("s-jan1", "2026-01-10T15:00:00");

            // Act
            var (_, kickoffs) = _parser.BuildEventMapsStreaming(html);

            // Assert
            Assert.Equal(new DateTime(2026, 1, 10, 15, 0, 0, DateTimeKind.Utc), kickoffs["s-jan1"]);
            Assert.Equal(DateTimeKind.Utc, kickoffs["s-jan1"].Kind);
        }

        [Fact]
        public void BuildEventMapsStreaming_ZSuffixedKickoff_IsKeptAsUtc()
        {
            // Arrange: an explicit UTC instant must pass through unchanged.
            var html = EscapedFixtureHtml("s-utc1", "2026-08-15T14:00:00Z");

            // Act
            var (_, kickoffs) = _parser.BuildEventMapsStreaming(html);

            // Assert
            Assert.Equal(new DateTime(2026, 8, 15, 14, 0, 0, DateTimeKind.Utc), kickoffs["s-utc1"]);
            Assert.Equal(DateTimeKind.Utc, kickoffs["s-utc1"].Kind);
        }

        [Fact]
        public void BuildEventMapsStreaming_ExplicitOffsetKickoff_ConvertsByOffset()
        {
            // Arrange: an explicit +01:00 offset defines the instant exactly.
            var html = EscapedFixtureHtml("s-off1", "2026-08-15T15:00:00+01:00");

            // Act
            var (_, kickoffs) = _parser.BuildEventMapsStreaming(html);

            // Assert
            Assert.Equal(new DateTime(2026, 8, 15, 14, 0, 0, DateTimeKind.Utc), kickoffs["s-off1"].ToUniversalTime());
        }
    }
}
