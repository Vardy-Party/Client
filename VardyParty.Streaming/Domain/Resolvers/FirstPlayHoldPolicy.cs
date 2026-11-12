namespace VardyParty.Streaming;

/// <summary>
/// The first-play hold (docs/PROBE_SCHEDULING_RULES.md R9, docs/STREAM_PLAYBACK_RULES.md):
/// a healthy catalog row waits until every lower try-order index has yielded, so
/// playback starts on the earlier row. An MP row never holds a later row while it is
/// still in <c>/mp</c>. MP chip siblings (index at or past the catalog count) join the
/// pool as soon as first play has happened. Shared by
/// <see cref="StreamResolutionOrchestrator"/> and the probe timeline harness so both
/// make the same decision.
/// </summary>
internal static class FirstPlayHoldPolicy
{
    /// <summary>First try-order index that has not yielded and is not an MP row.</summary>
    public static int Frontier(IReadOnlyList<bool> mpCatalog, IReadOnlySet<int> resolvedCatalog)
    {
        var frontier = 0;
        while (frontier < mpCatalog.Count
               && (resolvedCatalog.Contains(frontier) || mpCatalog[frontier]))
            frontier++;
        return frontier;
    }

    /// <summary>Whether a held healthy row at <paramref name="index"/> may join the pool now.</summary>
    public static bool IsReleasable(int index, int frontier, int catalogCount, bool hasPlayedFirstStream) =>
        index < frontier
        || frontier >= catalogCount
        || (index >= catalogCount && hasPlayedFirstStream);
}