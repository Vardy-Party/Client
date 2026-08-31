namespace VardyParty.Presentation;

/// <summary>
/// Applies a downloaded desktop package. Windows uses the MSIX
/// <c>PackageManager</c> APIs (OS shutdown + restart). Linux sideloads a
/// Snap Store-unpublished <c>.snap</c> after this process has exited.
/// </summary>
public interface IDesktopPackageApplier
{
    Task ApplyAsync(string localPackagePath, DesktopUpdateOffer offer, CancellationToken cancellationToken);
}
