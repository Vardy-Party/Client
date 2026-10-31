namespace VardyParty.Kernel;

/// <summary>
/// The resolve service could not give a verdict: it was unreachable, timed out,
/// failed itself, or refused the request. Nothing is known about the stream, so
/// callers must never mark it Failed, report it to crowd health, or drop it from
/// the pool because of this; they retry or defer it.
/// <para>
/// Kernel owns the base type so Playback (which cannot see Streaming) can tell
/// an infrastructure failure from "the service answered: no playlist" (a
/// <c>null</c> URL) on the fresh <c>/play</c> path.
/// </para>
/// </summary>
public class InfrastructureUnavailableException : Exception
{
    public InfrastructureUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// False when retrying straight away cannot help, e.g. the service has V2/MP
    /// scraping switched off. The row is still not a dead stream.
    /// </summary>
    public virtual bool IsRetryable => true;
}