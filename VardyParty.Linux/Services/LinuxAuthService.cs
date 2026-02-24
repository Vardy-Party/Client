using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VardyParty.Providers;
using VardyParty.Services;

namespace VardyParty.Linux.Services;

public class LinuxAuthService : IAuthTokenProvider, IAuthLoginService
{
    private readonly ILogger<LinuxAuthService> _logger;

    public LinuxAuthService(ILogger<LinuxAuthService> logger)
    {
        _logger = logger;
    }

    public bool HasValidToken => false;

    public Task<AuthLoginResult> LoginInteractiveAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("[LinuxAuthService] Interactive Auth0 login is not implemented for Linux yet.");
        return Task.FromResult(new AuthLoginResult(false, null, "Auth0 interactive login is not implemented for Linux yet."));
    }

    public Task<AuthDeviceLoginResult?> StartDeviceLoginAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("[LinuxAuthService] Device Auth0 login is not implemented for Linux yet.");
        return Task.FromResult<AuthDeviceLoginResult?>(null);
    }

    public Task<AuthLoginResult> PollDeviceLoginAsync(AuthDeviceCode deviceCode, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("[LinuxAuthService] Device Auth0 polling is not implemented for Linux yet.");
        return Task.FromResult(new AuthLoginResult(false, null, "Auth0 device login is not implemented for Linux yet."));
    }

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<bool> IsAuthenticatedAsync()
    {
        return Task.FromResult(false);
    }

    public Task LogoutAsync()
    {
        _logger.LogInformation("[LinuxAuthService] Logout called (no-op).");
        return Task.CompletedTask;
    }
}
