using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VardyParty.Models;
using VardyParty.Services;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;
using System.Text.Json;
using AutoFixture;
using Microsoft.Extensions.Options;
using VardyParty.Configuration;
using VardyParty.Resolvers;

namespace VardyParty.Core.Tests
{
    public class ApiServiceTests
    {
        private readonly Fixture _fixture = new();

        [Fact]
        public async Task GetAllGamesAsync_SetsLeagueFields()
        {
            var apiGames = new Dictionary<string, List<Game>>
            {
                { "SomeLeague", new List<Game> { new Game { Home = "H", Away = "A" } } }
            };
            
            var json = JsonSerializer.Serialize(apiGames);
            var handler = new FakeHttpMessageHandler(json);
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };

            var apiSettings= _fixture.Build<APISettings>().With(s => s.HeadlessBaseUrl, "https://test.local/").Create();

            var gameApiSettings = _fixture.Build<GamesApiSettings>()
                    .With(g => g.CallTimeoutSeconds, 10)
                    .With(g => g.MaxRetries, 0)
                    .Create();


            var streamResolver = new Mock<IStreamResolver>();
            var streamDeduplicator = new Mock<IStreamDeduplicator>();

            var api = new ApiService(client, NullLogger<ApiService>.Instance, streamResolver.Object,
                streamDeduplicator.Object, Options.Create(gameApiSettings), Options.Create(apiSettings));

            var all = await api.GetAllGamesAsync(forceRefresh: true);

            Assert.True(all.ContainsKey("SomeLeague"));
            Assert.Equal("SomeLeague", all["SomeLeague"][0].League);
            Assert.Equal("SomeLeague", all["SomeLeague"][0].ApiLeague);
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
