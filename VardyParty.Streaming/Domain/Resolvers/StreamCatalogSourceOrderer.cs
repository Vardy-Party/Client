using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Orders catalog streams so FB (English footybitex) candidates are tried before MP (v2) alternates.
/// </summary>
public static class StreamCatalogSourceOrderer
{
    public static List<StreamModel> OrderFbBeforeMp(IEnumerable<StreamModel> streams)
    {
        return streams
            .Select((stream, index) => (stream, index))
            .OrderBy(x => GetCatalogSourcePriority(x.stream))
            .ThenBy(x => x.index)
            .Select(x => x.stream)
            .ToList();
    }

    public static List<int> OrderIndexesFbBeforeMp(
        IReadOnlyList<int> indexes,
        Func<int, StreamModel> getStream)
    {
        var fb = new List<int>();
        var other = new List<int>();
        var mp = new List<int>();

        foreach (var index in indexes)
        {
            switch (getStream(index).ResolveCatalogSource())
            {
                case "fb":
                    fb.Add(index);
                    break;
                case "mp":
                    mp.Add(index);
                    break;
                default:
                    other.Add(index);
                    break;
            }
        }

        var ordered = new List<int>(indexes.Count);
        ordered.AddRange(fb);
        ordered.AddRange(other);
        ordered.AddRange(mp);
        return ordered;
    }

    internal static int GetCatalogSourcePriority(StreamModel stream) =>
        stream.ResolveCatalogSource() switch
        {
            "fb" => 0,
            "mp" => 1,
            _ => 2
        };
}
