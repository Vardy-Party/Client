namespace VardyParty.Presentation;

public interface IDesktopUpdateService
{
    DesktopUpdateOffer? Offer { get; }

    event Action<DesktopUpdateOffer?>? OfferChanged;

    void Start();

    Task InstallAsync(DesktopUpdateOffer offer, CancellationToken cancellationToken);
}
