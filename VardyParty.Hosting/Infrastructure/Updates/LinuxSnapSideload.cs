namespace VardyParty.Hosting;

/// <summary>
/// GitHub <c>--dangerous</c> snaps are local installs; snapd will not
/// auto-refresh them. A helper that is not running from the mounted snap
/// waits for this PID to exit, then installs and relaunches.
/// </summary>
public static class LinuxSnapSideload
{
    public const string SnapName = "vardyparty";

    public static string BuildWaitInstallRelaunchScript() =>
        $"""
        #!/bin/sh
        trap '' HUP
        set -eu
        target_pid="$1"
        package="$2"
        while kill -0 "$target_pid" 2>/dev/null; do
          sleep 0.25
        done
        pkexec snap install --dangerous --classic "$package"
        exec snap run {SnapName}
        """;
}
