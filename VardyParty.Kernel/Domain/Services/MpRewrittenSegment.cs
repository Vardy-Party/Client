namespace VardyParty.Kernel;

/// <summary>
/// Shape checks for LocalService <c>/mp</c> rewritten media URLs shared by
/// harvest readiness and the MP health probe builder.
/// </summary>
public static class MpRewrittenSegment
{
    /// <summary>
    /// True when <paramref name="url"/> looks like a captured rewritten
    /// segment the MP health probe can fetch (KDNS / <c>_s2=</c> / known hosts).
    /// </summary>
    public static bool LooksLike(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.Contains("_s2=", StringComparison.OrdinalIgnoreCase)
            || url.Contains("kdns.fr", StringComparison.OrdinalIgnoreCase)
            || url.Contains("fhlsport", StringComparison.OrdinalIgnoreCase)
            || url.Contains("isyjvux", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when at least one entry is a probe-shaped rewritten segment URL.
    /// </summary>
    public static bool HasProbeReady(IEnumerable<string>? urls) =>
        urls?.Any(LooksLike) == true;
}
