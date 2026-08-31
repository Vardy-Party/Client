using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using VardyParty.Presentation;

namespace VardyParty.Hosting;

public sealed class LinuxSnapSideloadApplier : IDesktopPackageApplier
{
    public Task ApplyAsync(string localPackagePath, DesktopUpdateOffer offer, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            ApplyLinux(localPackagePath, cancellationToken);
            return Task.CompletedTask;
        }

        throw new PlatformNotSupportedException("Snap sideload is Linux only.");
    }

    [SupportedOSPlatform("linux")]
    private static void ApplyLinux(string localPackagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(localPackagePath) || !File.Exists(localPackagePath))
        {
            throw new FileNotFoundException("Downloaded snap is missing.", localPackagePath);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var scriptPath = Path.Combine(
            Path.GetTempPath(),
            $"vardyparty-apply-update-{Guid.NewGuid():N}.sh");
        File.WriteAllText(scriptPath, LinuxSnapSideload.BuildWaitInstallRelaunchScript());
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var start = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Path.GetFullPath(localPackagePath));

        Process.Start(start);
    }
}

