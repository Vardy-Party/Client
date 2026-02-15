namespace VardyParty.Services;

public interface IAuthLoginService
{
    bool HasValidToken { get; }
    Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default);
    Task<AuthDeviceLoginResult?> StartDeviceLoginAsync(CancellationToken cancellationToken = default);
    Task<AuthLoginResult> PollDeviceLoginAsync(AuthDeviceCode deviceCode, CancellationToken cancellationToken = default);
}

public sealed record AuthLoginResult(bool IsSuccess, string? AccessToken, string? Error);

public sealed record AuthDeviceLoginResult(AuthDeviceCode DeviceCode);

public sealed record AuthDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    int ExpiresIn,
    int Interval,
    DateTimeOffset ExpiresAt);
