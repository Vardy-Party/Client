using VardyParty.Streaming;

namespace VardyParty.Hosting;

/// <summary>
/// What closing the direct channel means for one guest resolve.
/// A clean failure before the host is on <c>webrtc</c> stays on the relay
/// (<see cref="Decision.SignalFailed"/>, no fault). A drop once transport is
/// <c>webrtc</c>, and before <c>rpc-result</c>, is a user-facing fault. The
/// caller waits briefly so a close racing an in-flight result does not
/// overwrite success. After <c>rpc-result</c>, the close is ignored.
/// </summary>
internal static class DirectChannelClose
{
    internal readonly record struct Decision(bool SignalFailed, RemoteComputeFault? Fault);

    public static Decision Decide(bool readyAnnounced, bool useDirect, bool resultSeen, string correlationId)
    {
        if (resultSeen)
        {
            return new Decision(false, null);
        }

        if (!readyAnnounced || !useDirect)
        {
            return new Decision(true, null);
        }

        return new Decision(
            false,
            new RemoteComputeFault(
                "This phone",
                RemoteComputePlayClient.DirectConnectionDroppedMessage,
                correlationId));
    }

    /// <summary>
    /// Harvest chips arrive as <c>rpc-part</c> long before <c>rpc-result</c>.
    /// Only a chip with a playlist URL counts as a finished resolve.
    /// </summary>
    public static bool MarksResultSeen(MpPlayEvent? ev) =>
        ev is not null
        && string.Equals(ev.Type, "chip", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ev.Url);

    /// <summary>
    /// Empty first-chip wait: a mid-harvest drop must keep the correlation-id
    /// banner. CONNECT/DNS problems stay on this phone; a quiet miss does not.
    /// </summary>
    public static RemoteComputeFault EmptyHarvestFault(
        bool directDropped,
        string? egressProblem,
        string? harvestError,
        string correlationId)
    {
        if (directDropped)
        {
            return new RemoteComputeFault(
                "This phone",
                RemoteComputePlayClient.DirectConnectionDroppedMessage,
                correlationId);
        }

        if (!string.IsNullOrWhiteSpace(egressProblem))
        {
            return new RemoteComputeFault("This phone", egressProblem, correlationId);
        }

        return new RemoteComputeFault(
            "Local service",
            string.IsNullOrWhiteSpace(harvestError) ? "The remote PC returned no playlist" : harvestError,
            correlationId);
    }
}
