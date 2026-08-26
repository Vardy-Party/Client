using VardyParty.Auth;

namespace VardyParty.Desktop.Services;

/// <summary>
/// Placeholder token provider for the preview head. It never yields a token,
/// so authenticated API calls short-circuit to 401 and the homepage surfaces
/// its error banner. Wiring the shared Auth0 device-code/PKCE flow into this
/// head is a documented follow-up.
/// </summary>
public sealed class StubAuthTokenProvider : IAuthTokenProvider
{
    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false) =>
        Task.FromResult<string?>(null);

    public Task<bool> IsAuthenticatedAsync() => Task.FromResult(false);

    public Task LogoutAsync() => Task.CompletedTask;
}
