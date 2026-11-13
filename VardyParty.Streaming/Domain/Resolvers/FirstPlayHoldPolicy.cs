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

    /// <summary>
    /// Same, for MP-aware callers. A row holds at <paramref name="holdPosition"/>: an
    /// MP chip sibling (index at or past the catalog count) holds at its MP page's
    /// catalog place, so a healthy chip can be the first play once every earlier
    /// non-MP row has yielded, e.g. when every FB row is dead (rule 9). After first
    /// play every MP row - the catalog MP row, which is now its first chip (rule 6),
    /// and its siblings - joins at once, so chips keep the order they turned healthy
    /// in (rule 10).
    /// </summary>
    public static bool IsReleasable(
        int index,
        int holdPosition,
        IReadOnlyList<bool> mpCatalog,
        int frontier,
        bool hasPlayedFirstStream)
    {
        var catalogCount = mpCatalog.Count;
        var isMp = index >= catalogCount || mpCatalog[index];
        return holdPosition < frontier
               || frontier >= catalogCount
               || (isMp && hasPlayedFirstStream);
    }

    /// <summary>
    /// Hold place for a row at <paramref name="index"/>: its own index for catalog
    /// rows; for an MP chip sibling, the catalog index of the MP row on the same page.
    /// </summary>
    public static int HoldPosition(int index, string? pageUrl, IReadOnlyList<string?> catalogUrls, IReadOnlyList<bool> mpCatalog)
    {
        if (index < catalogUrls.Count || string.IsNullOrWhiteSpace(pageUrl))
            return index;

        for (var i = 0; i < catalogUrls.Count; i++)
        {
            if (mpCatalog[i] && string.Equals(catalogUrls[i], pageUrl, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return index;
    }
}