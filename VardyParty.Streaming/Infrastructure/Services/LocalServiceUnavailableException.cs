using VardyParty.Kernel;

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
    ConnectionFailed,

    /// <summary>
    /// The service answered with its own error (HTTP 5xx, or an NDJSON
    /// <c>error</c> line with a 5xx status) on <c>/mp</c>. Its Chrome or plugin
    /// failed; that says nothing about the stream. A 404 "M3U8 not captured"
    /// is still the stream's verdict.
    /// </summary>
    ServiceError,

    /// <summary>
    /// <c>/mp rejected: V2/MP scrape disabled</c> (HTTP 503 from LocalService
    /// when its MP gate is off). The row is unavailable here, not dead. The
    /// client does not try to re-enable V2/MP and does not retry at once.
    /// </summary>
    MpDisabled,

    /// <summary>
    /// The service refused our token (401) or role (403). A sign-in problem,
    /// not a dead stream.
    /// </summary>
    Unauthorized
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
    Exception? innerException = null) : InfrastructureUnavailableException(message, innerException)
{
    public LocalServiceUnavailableReason Reason { get; } = reason;

    /// <summary>MP-disabled and auth rejections do not clear up in seconds.</summary>
    public override bool IsRetryable =>
        Reason is not (LocalServiceUnavailableReason.MpDisabled or LocalServiceUnavailableReason.Unauthorized);
}