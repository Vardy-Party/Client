using System.Net.Http;

namespace VardyParty.Hosting;

/// <summary>
/// Wraps a factory-owned handler so a consumer (Auth0 OidcClient) can dispose
/// it without disposing the named DualStack pipeline.
/// </summary>
public sealed class NonDisposingDelegatingHandler : DelegatingHandler
{
    public NonDisposingDelegatingHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    protected override void Dispose(bool disposing)
    {
        // Skip base.Dispose — DelegatingHandler would dispose InnerHandler.
    }
}
