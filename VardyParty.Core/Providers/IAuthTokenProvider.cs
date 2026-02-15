namespace VardyParty.Providers;

public interface IAuthTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAuthenticatedAsync();
    Task LogoutAsync();
}
