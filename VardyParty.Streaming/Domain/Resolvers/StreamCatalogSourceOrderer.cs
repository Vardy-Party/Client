using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Orders catalog streams so MP (v2) candidates are tried before FB (footybites) alternates.
/// </summary>
public static class StreamCatalogSourceOrderer
{
    public static List<StreamModel> OrderMpBeforeFb(IEnumerable<StreamModel> streams)
    {
        return streams
            .Select((stream, index) => (stream, index))
            .OrderBy(x => GetCatalogSourcePriority(x.stream))
            .ThenBy(x => x.index)
            .Select(x => x.stream)
            .ToList();
    }

    public static List<int> OrderIndexesMpBeforeFb(
        IReadOnlyList<int> indexes,
        Func<int, StreamModel> getStream)
    {
        var mp = new List<int>();
        var fb = new List<int>();
        var other = new List<int>();

        foreach (var index in indexes)
        {
            switch (getStream(index).ResolveCatalogSource())
            {
                case "mp":
                    mp.Add(index);
                    break;
                case "fb":
                    fb.Add(index);
                    break;
                default:
                    other.Add(index);
                    break;
            }
        }

        var ordered = new List<int>(indexes.Count);
        ordered.AddRange(mp);
        ordered.AddRange(fb);
        ordered.AddRange(other);
        return ordered;
    }

    internal static int GetCatalogSourcePriority(StreamModel stream) =>
        stream.ResolveCatalogSource() switch
        {
            "mp" => 0,
            "fb" => 1,
            _ => 2
        };
}
