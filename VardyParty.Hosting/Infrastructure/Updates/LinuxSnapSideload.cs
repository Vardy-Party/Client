namespace VardyParty.Hosting;

/// <summary>
/// GitHub <c>--dangerous</c> snaps are local installs; snapd will not
/// auto-refresh them. A helper that is not running from the mounted snap
/// waits for this PID to exit, then installs and relaunches.
/// </summary>
public static class LinuxSnapSideload
{
    public const string SnapName = "vardyparty";
    public const int WaitForExitSeconds = 120;

    public static bool IsVardyPartySnap() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SNAP_NAME"),
            SnapName,
            StringComparison.Ordinal);

    public static string BuildWaitInstallRelaunchScript() =>
        $"""
        #!/bin/sh
        trap '' HUP TERM
        set -eu
        target_pid="$1"
        package="$2"
        deadline=$(( $(date +%s) + {WaitForExitSeconds} ))
        while kill -0 "$target_pid" 2>/dev/null; do
          if [ "$(date +%s)" -ge "$deadline" ]; then
            break
          fi
          sleep 0.25
        done
        status=0
        pkexec snap install --dangerous --classic "$package" || status=$?
        rm -f "$package" "$0"
        if [ "$status" -ne 0 ]; then
          exit "$status"
        fi
        exec snap run {SnapName}
        """;
}
