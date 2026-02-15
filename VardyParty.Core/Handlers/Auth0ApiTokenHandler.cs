using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using VardyParty.Providers;

namespace VardyParty.Services;

public class Auth0ApiTokenHandler(
    IAuthTokenProvider tokenProvider,
    ILogger<Auth0ApiTokenHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken);
        }

        logger.LogWarning("[Auth0] No access token available for {Method} {Url}", request.Method, request.RequestUri);
        return new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            RequestMessage = request
        };
    }
}
