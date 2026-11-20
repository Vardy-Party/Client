using System;
using System.Threading.Tasks;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// Waits for state the code under test is certain to reach but publishes without a
/// signal the test can await (for example the partner warm landing in
/// <see cref="WarmPlaybackUrlCache"/>). The timeout is a hang guard for a broken
/// build, not a guess at how long the work takes on a slow runner.
/// </summary>
internal static class AsyncWait
{
    /// <summary>Upper bound for any single wait in these tests.</summary>
    internal static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a test lets fire-and-forget work run before a negative check such as
    /// <c>Times.Never</c>. It can only shorten how long a regression gets to show; it can
    /// never make a correct build fail, so it is not a timing guess for a positive result.
    /// </summary>
    internal static readonly TimeSpan NegativeWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>Returns true as soon as <paramref name="condition"/> holds; false only after the hang guard.</summary>
    internal static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? HangGuard);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                return condition();
            await Task.Delay(10).ConfigureAwait(false);
        }

        return true;
    }
}
