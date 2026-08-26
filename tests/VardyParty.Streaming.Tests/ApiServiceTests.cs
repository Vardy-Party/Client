using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VardyParty.Kernel;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;
using System.Text.Json;
using AutoFixture;
using Microsoft.Extensions.Options;
using VardyParty.Streaming;
using VardyParty.TestSupport;

namespace VardyParty.Streaming.Tests
{
    public class ApiServiceTests
    {
        private readonly IFixture _fixture = AutoMoqFixture.Create();

        [Fact]
        public void NormalizeGames_UnspecifiedKind_IsStampedAsUtcWithoutShifting()
        {
            // Arrange: the API wire format is UTC, so an Unspecified kind must be
            // re-stamped, never run through ToUniversalTime (device offset).
            var wireTicks = new DateTime(2026, 8, 26, 14, 0, 0, DateTimeKind.Unspecified);
            var game = _fixture.Build<Game>().With(g => g.Start, wireTicks).Create();
            var dict = new Dictionary<string, List<Game>> { { "League Alpha", [game] } };

            // Act
            ApiService.NormalizeGames(dict);

            // Assert
            Assert.Equal(DateTimeKind.Utc, game.Start.Kind);
            Assert.Equal(wireTicks.Ticks, game.Start.Ticks);
        }

        [Fact]
        public void NormalizeGames_UtcKind_IsUnchanged()
        {
            // Arrange
            var startUtc = new DateTime(2026, 8, 26, 14, 0, 0, DateTimeKind.Utc);
            var game = _fixture.Build<Game>().With(g => g.Start, startUtc).Create();
            var dict = new Dictionary<string, List<Game>> { { "League Alpha", [game] } };

            // Act
            ApiService.NormalizeGames(dict);

            // Assert
            Assert.Equal(startUtc, game.Start);
            Assert.Equal(DateTimeKind.Utc, game.Start.Kind);
        }

        [Fact]
        public void NormalizeGames_LocalKind_IsConvertedToUtc()
        {
            // Arrange
            var startLocal = new DateTime(2026, 8, 26, 15, 0, 0, DateTimeKind.Local);
            var game = _fixture.Build<Game>().With(g => g.Start, startLocal).Create();
            var dict = new Dictionary<string, List<Game>> { { "League Alpha", [game] } };

            // Act
            ApiService.NormalizeGames(dict);

            // Assert
            Assert.Equal(DateTimeKind.Utc, game.Start.Kind);
            Assert.Equal(startLocal.ToUniversalTime(), game.Start);
        }

        [Fact]
        public async Task GetAllGamesAsync_SetsLeagueFields()
        {
            // Arrange
            var league = "SomeLeague";
            var game = _fixture.Build<Game>()
                .With(g => g.Home, "H")
                .With(g => g.Away, "A")
                .With(g => g.League, string.Empty)
                .With(g => g.ApiLeague, string.Empty)
                .Create();
            var apiGames = new Dictionary<string, List<Game>> { { league, [game] } };

            var json = JsonSerializer.Serialize(apiGames);
            var handler = new FakeHttpMessageHandler(json);
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };

            var apiSettings = _fixture.Build<APISettings>().With(s => s.HeadlessBaseUrl, "https://test.local/").Create();
            var gameApiSettings = _fixture.Build<GamesApiSettings>()
                .With(g => g.CallTimeoutSeconds, 10)
                .With(g => g.MaxRetries, 0)
                .Create();

            var api = new ApiService(
                client,
                NullLogger<ApiService>.Instance,
                _fixture.GetMock<ILocalLanPlayService>().Object,
                Options.Create(gameApiSettings),
                Options.Create(apiSettings));

            // Act
            var all = await api.GetAllGamesAsync(forceRefresh: true);

            // Assert
            Assert.True(all.ContainsKey(league));
            Assert.Equal(league, all[league][0].League);
            Assert.Equal(league, all[league][0].ApiLeague);
        }

        [Fact]
        public async Task GetStreamsAsync_ReputationWithNonCanonicalCasing_DeserializesWithoutThrowing()
        {
            // Arrange
            const string json = """{"href":"https://streams.example.com/match","streams":[{"url":"https://streams.example.com/1","channel":"Channel North","reputation":"Very good"}]}""";
            var handler = new FakeHttpMessageHandler(json);
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
            var apiSettings = _fixture.Build<APISettings>().With(s => s.HeadlessBaseUrl, "https://test.local/").Create();
            var gameApiSettings = _fixture.Build<GamesApiSettings>()
                .With(g => g.CallTimeoutSeconds, 10)
                .With(g => g.MaxRetries, 2)
                .Create();
            var api = new ApiService(
                client,
                NullLogger<ApiService>.Instance,
                _fixture.GetMock<ILocalLanPlayService>().Object,
                Options.Create(gameApiSettings),
                Options.Create(apiSettings));

            // Act
            var response = await api.GetStreamsAsync("League Alpha", "Home United", "Away City");

            // Assert
            Assert.NotNull(response);
            var stream = Assert.Single(response!.Streams);
            Assert.Equal(StreamReputation.VeryGood, stream.Reputation);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task GetStreamsAsync_MalformedJsonPayload_FailsFastWithoutRetrying()
        {
            // Arrange
            const string malformedJson = """{"href":"https://streams.example.com/match","streams":42}""";
            var handler = new FakeHttpMessageHandler(malformedJson);
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
            var apiSettings = _fixture.Build<APISettings>().With(s => s.HeadlessBaseUrl, "https://test.local/").Create();
            var gameApiSettings = _fixture.Build<GamesApiSettings>()
                .With(g => g.CallTimeoutSeconds, 10)
                .With(g => g.MaxRetries, 2)
                .Create();
            var api = new ApiService(
                client,
                NullLogger<ApiService>.Instance,
                _fixture.GetMock<ILocalLanPlayService>().Object,
                Options.Create(gameApiSettings),
                Options.Create(apiSettings));

            // Act
            var response = await api.GetStreamsAsync("League Alpha", "Home United", "Away City");

            // Assert
            Assert.Null(response);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public void IApiService_DoesNotExposeGetEnrichedStreamsAsync()
        {
            // Arrange
            var apiSurface = typeof(IApiService);

            // Act
            var removed = apiSurface.GetMethod("GetEnrichedStreamsAsync");

            // Assert
            Assert.Null(removed);
        }

        private class FakeHttpMessageHandler(string responseJson) : HttpMessageHandler
        {
            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                RequestCount++;
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            }
        }
    }
}
