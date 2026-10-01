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
}
