namespace VardyParty.Streaming;

/// <summary>Why the local service could not answer. None of these say anything about the stream.</summary>
public enum LocalServiceUnavailableReason
{
    /// <summary>UDP discovery found no service and no endpoint has worked yet.</summary>
    EndpointUnknown,

    /// <summary>
    /// Our own call timeout ran out. The service may still be resolving: at
    /// 16:49:54 UK time on 31 Oct 2026 it found the m3u8 after the 20s client
    /// timeout, twice.
    /// </summary>
    CallTimeout,

    /// <summary>The TCP connection to the local service failed.</summary>
    ConnectionFailed
}

/// <summary>
/// An infrastructure failure (local service unreachable, discovery miss, our
/// timeout), not a stream failure. Resolvers must never mark the stream Failed
/// or report it to the crowd-health API because of this; queue or retry it.
/// <see cref="LocalLanPlayService"/> throws it, and a <c>null</c> response still
/// means the service answered that the stream has no m3u8.
/// </summary>
public sealed class LocalServiceUnavailableException(
    LocalServiceUnavailableReason reason,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public LocalServiceUnavailableReason Reason { get; } = reason;
}