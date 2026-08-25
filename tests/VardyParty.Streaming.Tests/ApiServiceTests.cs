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
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            }
        }
    }
}
