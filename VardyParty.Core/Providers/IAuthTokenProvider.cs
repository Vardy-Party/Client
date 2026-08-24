namespace VardyParty.Providers;

public interface IAuthTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default, bool forceRefresh = false);
    Task<bool> IsAuthenticatedAsync();
    Task LogoutAsync();
}
