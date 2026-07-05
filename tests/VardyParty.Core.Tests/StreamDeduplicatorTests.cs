using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using VardyParty.Models;
using VardyParty.Resolvers;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class StreamDeduplicatorTests
    {
        private StreamDeduplicator CreateDeduplicator()
        {
            return new StreamDeduplicator(NullLogger<StreamDeduplicator>.Instance);
        }

        [Fact]
        public void ExtractBaseUrl_RemovesQueryString()
        {
            var dedup = CreateDeduplicator();
            var url = "https://example.com/stream.m3u8?token=abc123&expires=456";
            
            var result = dedup.ExtractBaseUrl(url);
            
            Assert.Equal("https://example.com/stream.m3u8", result);
        }

        [Fact]
        public void ExtractBaseUrl_NoQueryString_ReturnsUrlAsIs()
        {
            var dedup = CreateDeduplicator();
            var url = "https://example.com/stream.m3u8";
            
            var result = dedup.ExtractBaseUrl(url);
            
            Assert.Equal(url, result);
        }

        [Fact]
        public void ExtractBaseUrl_EmptyString_ReturnsEmpty()
        {
            var dedup = CreateDeduplicator();
            
            var result = dedup.ExtractBaseUrl("");
            
            Assert.Empty(result);
        }

        [Fact]
        public void DeduplicateStreams_SingleStream_ReturnsAsIs()
        {
            var dedup = CreateDeduplicator();
            var streams = new List<Models.Stream>
            {
                new Models.Stream { Url = "https://example.com/stream.m3u8", Channel = "Channel1" }
            };
            
            var result = dedup.DeduplicateStreams(streams);
            
            Assert.Single(result);
            Assert.Equal("Channel1", result[0].Channel);
        }

        [Fact]
        public void DeduplicateStreams_DuplicateUrlsWithDifferentQueryStrings_KeepsOne()
        {
            var dedup = CreateDeduplicator();
            var streams = new List<Models.Stream>
            {
                new Models.Stream { Url = "https://example.com/stream.m3u8?token=abc", Channel = "Channel1", Reputation = "Good" },
                new Models.Stream { Url = "https://example.com/stream.m3u8?token=xyz", Channel = "Channel2", Reputation = "OK" }
            };
            
            var result = dedup.DeduplicateStreams(streams);
            
            Assert.Single(result);
            Assert.Equal("Channel1", result[0].Channel); // Good reputation selected over OK
        }

        [Fact]
        public void DeduplicateStreams_MultipleUniquUrls_KeepsAll()
        {
            var dedup = CreateDeduplicator();
            var streams = new List<Models.Stream>
            {
                new Models.Stream { Url = "https://example1.com/stream.m3u8", Channel = "Channel1" },
                new Models.Stream { Url = "https://example2.com/stream.m3u8", Channel = "Channel2" },
                new Models.Stream { Url = "https://example3.com/stream.m3u8", Channel = "Channel3" }
            };
            
            var result = dedup.DeduplicateStreams(streams);
            
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public void DeduplicateStreams_MixedDuplicatesAndUnique_DeduplicatesCorrectly()
        {
            var dedup = CreateDeduplicator();
            var streams = new List<Models.Stream>
            {
                // Group 1: 2 duplicates with same base URL
                new Models.Stream { Url = "https://example1.com/stream.m3u8?token=abc", Channel = "Channel1", Reputation = "Good" },
                new Models.Stream { Url = "https://example1.com/stream.m3u8?token=xyz", Channel = "Channel1b", Reputation = "OK" },
                
                // Group 2: unique
                new Models.Stream { Url = "https://example2.com/stream.m3u8", Channel = "Channel2" },
                
                // Group 3: 3 duplicates with same base URL
                new Models.Stream { Url = "https://example3.com/stream.m3u8?a=1", Channel = "Channel3a", Reputation = "Very Good" },
                new Models.Stream { Url = "https://example3.com/stream.m3u8?a=2", Channel = "Channel3b", Reputation = "Good" },
                new Models.Stream { Url = "https://example3.com/stream.m3u8?a=3", Channel = "Channel3c", Reputation = "Poor" }
            };
            
            var result = dedup.DeduplicateStreams(streams);
            
            Assert.Equal(3, result.Count); // 3 unique base URLs
            
            // Verify correct streams were selected
            var channels = result.Select(s => s.Channel).OrderBy(c => c).ToList();
            Assert.True(channels.Contains("Channel1"));      // Good reputation selected
            Assert.True(channels.Contains("Channel2"));      // Only one, kept as-is
            Assert.True(channels.Contains("Channel3a"));     // Very Good reputation selected
        }

        [Fact]
        public void DeduplicateStreams_ReputationOrdering()
        {
            var dedup = CreateDeduplicator();
            var streams = new List<Models.Stream>
            {
                new Models.Stream { Url = "https://example.com/stream.m3u8?v=1", Channel = "Poor", Reputation = "Poor" },
                new Models.Stream { Url = "https://example.com/stream.m3u8?v=2", Channel = "VeryGood", Reputation = "Very Good" },
                new Models.Stream { Url = "https://example.com/stream.m3u8?v=3", Channel = "Good", Reputation = "Good" },
                new Models.Stream { Url = "https://example.com/stream.m3u8?v=4", Channel = "OK", Reputation = "OK" }
            };
            
            var result = dedup.DeduplicateStreams(streams);
            
            Assert.Single(result);
            Assert.Equal("VeryGood", result[0].Channel); // Highest reputation selected
        }

        [Fact]
        public void DeduplicateStreams_EmptyList_ReturnsEmpty()
        {
            var dedup = CreateDeduplicator();
            var streams = new List<Models.Stream>();
            
            var result = dedup.DeduplicateStreams(streams);
            
            Assert.Empty(result);
        }

        [Fact]
        public void DeduplicateStreams_NullList_ReturnsEmpty()
        {
            var dedup = CreateDeduplicator();
            
            var result = dedup.DeduplicateStreams(null!);
            
            Assert.Empty(result);
        }

        [Fact]
        public void DeduplicateStreams_V2SameUrlDifferentPlayerLabels_KeepsAll()
        {
            var dedup = CreateDeduplicator();
            var streams = new List<Models.Stream>
            {
                new Models.Stream
                {
                    Url = "https://madplay.example/match",
                    Channel = "Fola ID",
                    PlayerStream = "Fola ID",
                    ResolutionStrategy = "v2"
                },
                new Models.Stream
                {
                    Url = "https://madplay.example/match",
                    Channel = "Fubo US",
                    PlayerStream = "Fubo US",
                    ResolutionStrategy = "v2"
                }
            };

            var result = dedup.DeduplicateStreams(streams);

            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void DeduplicateStreams_CaseInsensitiveUrlMatching()
        {
            var dedup = CreateDeduplicator();
            var streams = new List<Models.Stream>
            {
                new Models.Stream { Url = "https://EXAMPLE.COM/stream.m3u8?v=1", Channel = "Channel1" },
                new Models.Stream { Url = "https://example.com/stream.m3u8?v=2", Channel = "Channel2" }
            };
            
            var result = dedup.DeduplicateStreams(streams);
            
            // Should deduplicate despite case difference in domain
            Assert.Single(result);
        }
    }
}
