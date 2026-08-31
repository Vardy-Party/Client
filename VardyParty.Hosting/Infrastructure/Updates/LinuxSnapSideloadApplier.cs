using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using VardyParty.Presentation;

namespace VardyParty.Hosting;

public sealed class LinuxSnapSideloadApplier : IDesktopPackageApplier
{
    public Task<DesktopApplyResult> ApplyAsync(
        string localPackagePath,
        DesktopUpdateOffer offer,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Snap sideload is Linux only.");
        }

        if (!LinuxSnapSideload.IsVardyPartySnap())
        {
            throw new InvalidOperationException(
                "Snap updates apply only when this app is installed as the vardyparty snap.");
        }

        ApplyLinux(localPackagePath, cancellationToken);
        return Task.FromResult(new DesktopApplyResult(CallerShouldQuit: true));
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
            FileName = "/usr/bin/setsid",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("/bin/sh");
        start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(Path.GetFullPath(localPackagePath));

        using var process = Process.Start(start);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to detach the snap update helper.");
        }
    }
}
