using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Configuration;
using Xunit;

namespace VardyParty.Tests;

public class Auth0OAuthClientTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void BuildUrl_StripsSchemeAndTrailingSlash()
    {
        // Arrange
        var domain = "https://id.northgate.test/";

        // Act
        var url = Auth0OAuthClient.BuildUrl(domain, "/oauth/token");

        // Assert
        Assert.Equal("https://id.northgate.test/oauth/token", url);
    }

    [Fact]
    public async Task RequestDeviceCodeAsync_WhenAuth0ReturnsCodes_MapsDeviceLogin()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var body =
            """
            {"device_code":"device-oak","user_code":"WD4K-7Q2P","verification_uri":"https://id.northgate.test/activate","verification_uri_complete":"https://id.northgate.test/activate?user_code=WD4K-7Q2P","expires_in":600,"interval":5}
            """;
        var inner = new JsonHandler(HttpStatusCode.OK, body);
        using var http = new HttpClient(inner);
        var factory = _fixture.GetMock<IHttpClientFactory>();
        factory.Setup(clientFactory => clientFactory.CreateClient(Auth0HttpClients.Name)).Returns(http);
        var sut = new Auth0OAuthClient(factory.Object, NullLogger<Auth0OAuthClient>.Instance);

        // Act
        var result = await sut.RequestDeviceCodeAsync(settings, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("WD4K-7Q2P", result.DeviceCode!.UserCode);
        Assert.Equal("device-oak", result.DeviceCode.DeviceCode);
        Assert.Contains("/oauth/device/code", inner.LastRequestUri?.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeDeviceCodeAsync_WhenAuthorizationPending_SurfacesErrorCode()
    {
        // Arrange
        var settings = CreateNorthgateSettings();
        var body = """{"error":"authorization_pending","error_description":"still waiting for oak-lane"}""";
        var inner = new JsonHandler(HttpStatusCode.BadRequest, body);
        using var http = new HttpClient(inner);
        var factory = _fixture.GetMock<IHttpClientFactory>();
        factory.Setup(clientFactory => clientFactory.CreateClient(Auth0HttpClients.Name)).Returns(http);
        var sut = new Auth0OAuthClient(factory.Object, NullLogger<Auth0OAuthClient>.Instance);

        // Act
        var result = await sut.ExchangeDeviceCodeAsync(settings, "device-oak", CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("authorization_pending", result.Error);
    }

    private Auth0Settings CreateNorthgateSettings()
        => _fixture.Build<Auth0Settings>()
            .With(settings => settings.Domain, "id.northgate.test")
            .With(settings => settings.ClientId, "northgate-desktop")
            .With(settings => settings.Audience, "https://catalog.northgate.test")
            .With(settings => settings.Scope, "openid profile")
            .Create();

    private sealed class JsonHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
