namespace VardyParty.Presentation;

public interface IDesktopUpdateService
{
    DesktopUpdateOffer? Offer { get; }

    event Action<DesktopUpdateOffer?>? OfferChanged;

    event Action<string>? ApplyFailed;

    void Start();

    Task InstallAsync(DesktopUpdateOffer offer, CancellationToken cancellationToken);
}
