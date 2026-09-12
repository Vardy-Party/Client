using VardyParty.Kernel;
using VardyParty.LocalService.Abstractions;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Expands v2 API stream entries (one page URL, many player labels) into per-label candidates.
/// The worker catalogs the href only; chips are discovered by LocalService <c>POST /mp</c>.
/// A v2 row with a page URL and no labels is still a candidate (first chip / autoplay).
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

                // API isV2StreamPlayable is URL + v2 — chips stay on the resolver.
                if (labels.Count == 0)
                {
                    if (stream.IsReadyForLocalResolution)
                    {
                        expanded.Add(stream);
                    }

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

    /// <summary>
    /// Extra chip rows after LocalService lists labels on a match page.
    /// Skips the chip already resolved in this pass.
    /// </summary>
    public static List<StreamModel> RemainingFromLocalService(
        StreamModel source,
        IEnumerable<string>? discoveredChips,
        string? selectedChip,
        IDiscoveredChipNormalizer chipNormalizer)
    {
        ArgumentNullException.ThrowIfNull(chipNormalizer);

        var labels = chipNormalizer.Normalize(discoveredChips).ToList();
        if (labels.Count == 0)
        {
            return [];
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(selectedChip))
        {
            taken.Add(selectedChip.Trim());
        }

        if (!string.IsNullOrWhiteSpace(source.PlayerStream))
        {
            taken.Add(source.PlayerStream.Trim());
        }

        return labels
            .Where(label => taken.Add(label))
            .Select(label => CloneWithPlayerStream(source, label))
            .ToList();
    }

    public static void ApplySelectedChip(StreamModel stream, string? selectedChip)
    {
        if (string.IsNullOrWhiteSpace(selectedChip) || !string.IsNullOrWhiteSpace(stream.PlayerStream))
        {
            return;
        }

        var label = selectedChip.Trim();
        stream.PlayerStream = label;
        stream.Channel = label;
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
