namespace VardyParty.Presentation;

public sealed class NullDesktopUpdateService : IDesktopUpdateService
{
    public DesktopUpdateOffer? Offer => null;

    public event Action<DesktopUpdateOffer?>? OfferChanged
    {
        add { }
        remove { }
    }

    public event Action<string>? ApplyFailed
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public Task InstallAsync(DesktopUpdateOffer offer, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
