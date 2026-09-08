using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Expands v2 API stream entries. Legacy rows with player labels become
/// per-label candidates. Slim MP rows (url + v2 strategy only) stay as one candidate.
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

                if (labels.Count == 0)
                {
                    // Slim API rows omit playerStream/channel — do not invent a LAN label.
                    expanded.Add(CloneWithPlayerStream(stream, string.Empty));
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
