namespace VardyParty.Presentation;

public enum DesktopPendingUpdateState
{
    None,
    Applied,
    FailedToApply,
}

/// <summary>
/// After a desktop replace, the next process compares the running assembly
/// (source of truth) to the version we asked the OS/snapd to install.
/// </summary>
public static class DesktopPendingUpdatePolicy
{
    public static DesktopPendingUpdateState Evaluate(
        AppReleaseVersion running,
        AppReleaseVersion? pendingExpected)
    {
        if (pendingExpected is null)
        {
            return DesktopPendingUpdateState.None;
        }

        return running.CompareTo(pendingExpected.Value) >= 0
            ? DesktopPendingUpdateState.Applied
            : DesktopPendingUpdateState.FailedToApply;
    }
}
