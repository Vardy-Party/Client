namespace VardyParty.Ports;

/// <summary>Settings actions for sharing this PC or redeeming an invite code.</summary>
public interface IRemoteComputeController
{
    bool ShareEnabled { get; }

    string InviteCode { get; }

    string Status { get; }

    event Action? Changed;

    Task SetShareEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task RedeemAsync(string code, CancellationToken cancellationToken = default);
}

public sealed class NullRemoteComputeController : IRemoteComputeController
{
    public bool ShareEnabled => false;

    public string InviteCode => "";

    public string Status => "";

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task SetShareEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RedeemAsync(string code, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
