namespace VardyParty.Hosting;

/// <summary>
/// What the sharing device does with one <c>/compute/host/status</c> answer.
/// </summary>
public enum HostWatchAction
{
    /// <summary>Keep polling.</summary>
    Keep,

    /// <summary>The invite was redeemed; clear it and keep polling.</summary>
    InviteUsed,

    /// <summary>The local-service is up but not hosting (it restarted); run the host start again.</summary>
    Restart,

    /// <summary>The local-service did not answer; wait for it to come back.</summary>
    Offline
}

/// <summary>
/// A restarted local-service drops its relay host socket, and the sharing
/// device only learns that from the status answer. Pure decision for the poll.
/// </summary>
public static class HostWatch
{
    public static HostWatchAction Decide(bool reachable, bool? sharing, bool inviteUsed, bool inviteOutstanding)
    {
        if (!reachable)
        {
            return HostWatchAction.Offline;
        }

        if (sharing == false)
        {
            return HostWatchAction.Restart;
        }

        if (inviteUsed && inviteOutstanding)
        {
            return HostWatchAction.InviteUsed;
        }

        return HostWatchAction.Keep;
    }
}
