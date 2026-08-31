using System.Diagnostics;
using VardyParty.Presentation;

namespace VardyParty.Hosting;

public sealed class ProcessDesktopInstallerLauncher : IDesktopInstallerLauncher
{
    public void LaunchDownloadedInstaller(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            throw new FileNotFoundException("Downloaded installer is missing.", localPath);
        }

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = localPath,
                UseShellExecute = true,
            });
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "pkexec",
                ArgumentList = { "snap", "install", "--dangerous", "--classic", localPath },
                UseShellExecute = false,
            });
            return;
        }

        throw new PlatformNotSupportedException("Desktop updates are Windows and Linux only.");
    }
}
