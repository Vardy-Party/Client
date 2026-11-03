using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Legacy helpers. Catalog try-order is first-come-first-served (recommendations
/// then remaining indexes); these no longer reshape MP ahead of FB.
/// </summary>
public static class StreamCatalogSourceOrderer
{
    /// <summary>Preserves input order (FB and MP stay mixed).</summary>
    public static List<StreamModel> OrderMpBeforeFb(IEnumerable<StreamModel> streams)
        => streams.ToList();

    /// <summary>Preserves index order (FB and MP stay mixed).</summary>
    public static List<int> OrderIndexesMpBeforeFb(
        IReadOnlyList<int> indexes,
        Func<int, StreamModel> getStream)
    {
        _ = getStream;
        return indexes.ToList();
    }
}
