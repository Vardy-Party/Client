namespace VardyParty.Ports;

/// <summary>Settings actions for sharing this PC or redeeming an invite code.</summary>
public interface IRemoteComputeController
{
    bool ShareEnabled { get; }

    /// <summary>This device redeemed another computer's invite.</summary>
    bool IsGuestPaired { get; }

    /// <summary>False after this API returned 404 for the compute routes.</summary>
    bool OffersRemoteCompute { get; }

    /// <summary>The signed-in token has the relay-user role or permission.</summary>
    bool HasRelayUser { get; }

    string InviteCode { get; }

    string Status { get; }

    /// <summary>True while an invite code is being checked with the relay.</summary>
    bool RedeemBusy { get; }

    event Action? Changed;

    Task SetShareEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task RedeemAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Leave "use another user's local-service" and return to neither.</summary>
    Task StopUsingRemoteAsync(CancellationToken cancellationToken = default);

    /// <summary>Read relay-user from the current access token.</summary>
    Task RefreshAccessAsync(CancellationToken cancellationToken = default);
}

public sealed class NullRemoteComputeController : IRemoteComputeController
{
    public bool ShareEnabled => false;

    public bool IsGuestPaired => false;

    public bool OffersRemoteCompute => true;

    public bool HasRelayUser => false;

    public string InviteCode => "";

    public string Status => "";

    public bool RedeemBusy => false;

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task SetShareEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RedeemAsync(string code, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task StopUsingRemoteAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RefreshAccessAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
