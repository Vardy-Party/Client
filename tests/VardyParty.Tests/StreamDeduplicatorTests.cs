using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using VardyParty.Models;
using VardyParty.Resolvers;
using Xunit;

namespace VardyParty.Tests
{
    public class StreamDeduplicatorTests
    {
        private readonly IFixture _fixture = AutoMoqFixture.Create();

        private StreamDeduplicator CreateDeduplicator()
        {
            return new StreamDeduplicator(NullLogger<StreamDeduplicator>.Instance);
        }

        [Fact]
        public void ExtractBaseUrl_RemovesQueryString()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var url = "https://streams.example.com/stream.m3u8?token=abc123&expires=456";

            // Act
            var result = dedup.ExtractBaseUrl(url);

            // Assert
            Assert.Equal("https://streams.example.com/stream.m3u8", result);
        }

        [Fact]
        public void ExtractBaseUrl_NoQueryString_ReturnsUrlAsIs()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var url = "https://streams.example.com/stream.m3u8";

            // Act
            var result = dedup.ExtractBaseUrl(url);

            // Assert
            Assert.Equal(url, result);
        }

        [Fact]
        public void ExtractBaseUrl_EmptyString_ReturnsEmpty()
        {
            // Arrange
            var dedup = CreateDeduplicator();

            // Act
            var result = dedup.ExtractBaseUrl("");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void DeduplicateStreams_SingleStream_ReturnsAsIs()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var streams = new List<Stream>
            {
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/stream.m3u8")
                    .With(s => s.Channel, "Channel North")
                    .With(s => s.Reputation, string.Empty)
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create()
            };

            // Act
            var result = dedup.DeduplicateStreams(streams);

            // Assert
            Assert.Single(result);
            Assert.Equal("Channel North", result[0].Channel);
        }

        [Fact]
        public void DeduplicateStreams_DuplicateUrlsWithDifferentQueryStrings_KeepsOne()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var streams = new List<Stream>
            {
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/stream.m3u8?token=abc")
                    .With(s => s.Channel, "Channel North")
                    .With(s => s.Reputation, "Good")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/stream.m3u8?token=xyz")
                    .With(s => s.Channel, "Channel South")
                    .With(s => s.Reputation, "OK")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create()
            };

            // Act
            var result = dedup.DeduplicateStreams(streams);

            // Assert
            Assert.Single(result);
            Assert.Equal("Channel North", result[0].Channel);
        }

        [Fact]
        public void DeduplicateStreams_MultipleUniquUrls_KeepsAll()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var streams = new List<Stream>
            {
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://alpha.example.com/stream.m3u8")
                    .With(s => s.Channel, "Channel North")
                    .With(s => s.Reputation, string.Empty)
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://beta.example.com/stream.m3u8")
                    .With(s => s.Channel, "Channel South")
                    .With(s => s.Reputation, string.Empty)
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://gamma.example.com/stream.m3u8")
                    .With(s => s.Channel, "Channel East")
                    .With(s => s.Reputation, string.Empty)
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create()
            };

            // Act
            var result = dedup.DeduplicateStreams(streams);

            // Assert
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public void DeduplicateStreams_MixedDuplicatesAndUnique_DeduplicatesCorrectly()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var streams = new List<Stream>
            {
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://alpha.example.com/stream.m3u8?token=abc")
                    .With(s => s.Channel, "Channel North")
                    .With(s => s.Reputation, "Good")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://alpha.example.com/stream.m3u8?token=xyz")
                    .With(s => s.Channel, "Channel North B")
                    .With(s => s.Reputation, "OK")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://beta.example.com/stream.m3u8")
                    .With(s => s.Channel, "Channel South")
                    .With(s => s.Reputation, string.Empty)
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://gamma.example.com/stream.m3u8?a=1")
                    .With(s => s.Channel, "Channel East")
                    .With(s => s.Reputation, "Very Good")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://gamma.example.com/stream.m3u8?a=2")
                    .With(s => s.Channel, "Channel East B")
                    .With(s => s.Reputation, "Good")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://gamma.example.com/stream.m3u8?a=3")
                    .With(s => s.Channel, "Channel East C")
                    .With(s => s.Reputation, "Poor")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create()
            };

            // Act
            var result = dedup.DeduplicateStreams(streams);

            // Assert
            Assert.Equal(3, result.Count);

            var channels = result.Select(s => s.Channel).OrderBy(c => c).ToList();
            Assert.Contains("Channel North", channels);
            Assert.Contains("Channel South", channels);
            Assert.Contains("Channel East", channels);
        }

        [Fact]
        public void DeduplicateStreams_ReputationOrdering()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var streams = new List<Stream>
            {
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/stream.m3u8?v=1")
                    .With(s => s.Channel, "Channel Poor")
                    .With(s => s.Reputation, "Poor")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/stream.m3u8?v=2")
                    .With(s => s.Channel, "Channel Best")
                    .With(s => s.Reputation, "Very Good")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/stream.m3u8?v=3")
                    .With(s => s.Channel, "Channel Good")
                    .With(s => s.Reputation, "Good")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/stream.m3u8?v=4")
                    .With(s => s.Channel, "Channel Ok")
                    .With(s => s.Reputation, "OK")
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create()
            };

            // Act
            var result = dedup.DeduplicateStreams(streams);

            // Assert
            Assert.Single(result);
            Assert.Equal("Channel Best", result[0].Channel);
        }

        [Fact]
        public void DeduplicateStreams_EmptyList_ReturnsEmpty()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var streams = new List<Stream>();

            // Act
            var result = dedup.DeduplicateStreams(streams);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void DeduplicateStreams_NullList_ReturnsEmpty()
        {
            // Arrange
            var dedup = CreateDeduplicator();

            // Act
            var result = dedup.DeduplicateStreams(null!);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void DeduplicateStreams_V2SameUrlDifferentPlayerLabels_KeepsAll()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var streams = new List<Stream>
            {
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/match")
                    .With(s => s.Channel, "Channel North")
                    .With(s => s.PlayerStream, "Channel North")
                    .With(s => s.ResolutionStrategy, "v2")
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/match")
                    .With(s => s.Channel, "Channel South")
                    .With(s => s.PlayerStream, "Channel South")
                    .With(s => s.ResolutionStrategy, "v2")
                    .Create()
            };

            // Act
            var result = dedup.DeduplicateStreams(streams);

            // Assert
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void DeduplicateStreams_CaseInsensitiveUrlMatching()
        {
            // Arrange
            var dedup = CreateDeduplicator();
            var streams = new List<Stream>
            {
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://STREAMS.EXAMPLE.COM/stream.m3u8?v=1")
                    .With(s => s.Channel, "Channel North")
                    .With(s => s.Reputation, string.Empty)
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create(),
                _fixture.Build<Stream>()
                    .With(s => s.Url, "https://streams.example.com/stream.m3u8?v=2")
                    .With(s => s.Channel, "Channel South")
                    .With(s => s.Reputation, string.Empty)
                    .With(s => s.ResolutionStrategy, string.Empty)
                    .Create()
            };

            // Act
            var result = dedup.DeduplicateStreams(streams);

            // Assert
            Assert.Single(result);
        }
    }
}
