using VardyParty.Models;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Playback try-order: crowd-recommended streams first, then FB before MP.
/// The control panel badges a stream as recommended whenever it is in the
/// recommended list (successes within the 2-hour health window). Confidence
/// is only a freshness signal and must not discard that list.
/// </summary>
public static class StreamTestOrderPolicy
{
    public static bool ShouldPreferRecommendations(RecommendationResponse? recommendations) =>
        recommendations?.Recommended is { Count: > 0 };

    public static List<int> Build(
        RecommendationResponse? recommendations,
        int totalStreams,
        Func<string, string?, int> resolveIndex,
        Func<int, StreamModel> getStream)
    {
        if (totalStreams <= 0)
        {
            return [];
        }

        var ordered = new List<int>(totalStreams);
        var seen = new HashSet<int>();

        if (ShouldPreferRecommendations(recommendations))
        {
            foreach (var recommendedItem in recommendations!.Recommended)
            {
                if (string.IsNullOrWhiteSpace(recommendedItem.Url))
                {
                    continue;
                }

                var index = resolveIndex(recommendedItem.Url, recommendedItem.StreamName);
                if (index < 0 || index >= totalStreams || !seen.Add(index))
                {
                    continue;
                }

                ordered.Add(index);
            }
        }

        var remainder = new List<int>(totalStreams - ordered.Count);
        for (var i = 0; i < totalStreams; i++)
        {
            if (!seen.Contains(i))
            {
                remainder.Add(i);
            }
        }

        ordered.AddRange(StreamCatalogSourceOrderer.OrderIndexesFbBeforeMp(remainder, getStream));
        return ordered;
    }
}
