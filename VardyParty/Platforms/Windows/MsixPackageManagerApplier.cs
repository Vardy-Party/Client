using System.Runtime.InteropServices;
using VardyParty.Presentation;
using Windows.Management.Deployment;

namespace VardyParty.Platforms.Windows;

/// <summary>
/// Sideloaded MSIX self-update:
/// <see href="https://learn.microsoft.com/en-us/windows/msix/non-store-developer-updates"/>.
/// Non-UWP packaged apps must call <c>RegisterApplicationRestart</c> before
/// <c>AddPackageAsync</c> with <c>ForceApplicationShutdown</c>.
/// </summary>
public sealed class MsixPackageManagerApplier : IDesktopPackageApplier
{
    public async Task<DesktopApplyResult> ApplyAsync(
        string localPackagePath,
        DesktopUpdateOffer offer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(localPackagePath) || !File.Exists(localPackagePath))
        {
            throw new FileNotFoundException("Downloaded MSIX is missing.", localPackagePath);
        }

        _ = global::Windows.ApplicationModel.Package.Current.Id.FullName;

        var packageUri = new Uri(Path.GetFullPath(localPackagePath));
        var restart = NativeMethods.RegisterApplicationRestart(null, NativeMethods.RestartFlags.None);
        if (restart != 0)
        {
            throw new InvalidOperationException(
                $"RegisterApplicationRestart failed (0x{restart:X8}); refusing to force-shutdown without a restart.");
        }

        var manager = new PackageManager();
        var result = await manager.AddPackageAsync(
                packageUri,
                Array.Empty<Uri>(),
                DeploymentOptions.ForceApplicationShutdown | DeploymentOptions.ForceTargetApplicationShutdown)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (result.ExtendedErrorCode is { HResult: not 0 } error)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.ErrorText)
                    ? $"MSIX update failed (0x{error.HResult:X8})."
                    : result.ErrorText,
                error);
        }

        return new DesktopApplyResult(CallerShouldQuit: false);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterApplicationRestart(string? pwzCommandLine, RestartFlags dwFlags);

        [Flags]
        internal enum RestartFlags
        {
            None = 0,
        }
    }
}
