using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace VardyParty.Auth;

public class Auth0ApiTokenHandler(
    IAuthTokenProvider tokenProvider,
    ILogger<Auth0ApiTokenHandler> logger) : DelegatingHandler
{
    internal static readonly TimeSpan TokenFetchTimeout = TimeSpan.FromSeconds(20);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var tokenCts = new CancellationTokenSource(TokenFetchTimeout);
        var token = await tokenProvider.GetAccessTokenAsync(tokenCts.Token, forceRefresh: false);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger.LogWarning("[Auth0] No access token available for {Method} {Url}", request.Method, request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                RequestMessage = request
            };
        }

        await BufferContentAsync(request, cancellationToken);
        ApplyBearer(request, token);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        using var refreshCts = new CancellationTokenSource(TokenFetchTimeout);
        var refreshed = await tokenProvider.GetAccessTokenAsync(refreshCts.Token, forceRefresh: true);
        if (string.IsNullOrWhiteSpace(refreshed) || string.Equals(refreshed, token, StringComparison.Ordinal))
        {
            return response;
        }

        logger.LogInformation("[Auth0] Retrying {Method} {Url} after access-token refresh", request.Method, request.RequestUri);
        response.Dispose();
        var retry = await CloneRequestAsync(request, cancellationToken);
        ApplyBearer(retry, refreshed);
        return await base.SendAsync(retry, cancellationToken);
    }

    private static void ApplyBearer(HttpRequestMessage request, string token)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static async Task BufferContentAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content == null)
        {
            return;
        }

        var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentHeaders = request.Content.Headers.ToArray();
        request.Content = new ByteArrayContent(bytes);
        foreach (var header in contentHeaders)
        {
            request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }

        if (request.Content == null)
        {
            return clone;
        }

        var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        clone.Content = new ByteArrayContent(bytes);
        foreach (var header in request.Content.Headers)
        {
            clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
