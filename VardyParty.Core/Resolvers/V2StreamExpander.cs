using VardyParty.Models;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Resolvers;

/// <summary>
/// Expands v2 API stream entries (one page URL, many player labels) into per-label candidates.
/// v2 rows with no player-stream labels are dropped — an empty MP page shell is not testable.
/// </summary>
public static class V2StreamExpander
{
    public static List<StreamModel> Expand(IEnumerable<StreamModel> streams)
    {
        var expanded = new List<StreamModel>();
        foreach (var stream in streams)
        {
            if (stream.RequiresV2StreamSelection)
            {
                var labels = stream.PlayerStreams
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(label => label.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // No labels => not a real MP candidate (matches API isV2StreamPlayable).
                if (labels.Count == 0)
                {
                    continue;
                }

                foreach (var label in labels)
                {
                    expanded.Add(CloneWithPlayerStream(stream, label));
                }

                continue;
            }

            expanded.Add(stream);
        }

        return expanded;
    }

    private static StreamModel CloneWithPlayerStream(StreamModel source, string playerStreamLabel) =>
        new()
        {
            Url = source.Url,
            Channel = playerStreamLabel,
            PlayerStream = playerStreamLabel,
            ResolutionStrategy = source.ResolutionStrategy,
            Reputation = source.Reputation,
            Quality = source.Quality,
            Language = source.Language,
            Ads = source.Ads,
            StreamStatus = source.StreamStatus,
            PlayerStreams = source.PlayerStreams,
            BitrateKbps = source.BitrateKbps,
            Resolution = source.Resolution
        };
}
