namespace VardyParty.Kernel;

/// <summary>
/// One rule for a fresh <c>/play</c> (or <c>/mp</c>) that hits
/// <see cref="InfrastructureUnavailableException"/>, shared by the first start
/// (StreamResolutionOrchestrator) and in-player retries (PlaybackPoolCommandActions)
/// on every host: wait and ask again, never report or drop the stream. After the
/// last wait the caller defers: the row stays in the pool, unreported.
/// </summary>
public static class InfrastructureRetryPolicy
{
    /// <summary>Waits before each extra fresh-resolve attempt (about 22s in all).</summary>
    public static readonly TimeSpan[] FreshResolveDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15)
    ];

    /// <summary>
    /// True when <paramref name="attempt"/> (0-based, already failed) should be
    /// followed by another try. Non-retryable failures (MP disabled, auth) defer at once.
    /// </summary>
    public static bool ShouldRetryFreshResolve(InfrastructureUnavailableException exception, int attempt)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.IsRetryable && attempt >= 0 && attempt < FreshResolveDelays.Length;
    }
}