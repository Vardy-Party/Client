using VardyParty.Models;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Playback try-order: crowd-recommended streams first (high confidence
/// before low), then FB before MP. The control panel badges a stream as
/// recommended whenever it is in the recommended list; confidence ranks
/// that list instead of discarding it.
/// </summary>
public static class StreamTestOrderPolicy
{
    public static bool ShouldPreferRecommendations(RecommendationResponse? recommendations) =>
        recommendations?.Recommended is { Count: > 0 };

    public static int RankConfidence(RecommendationConfidence confidence) =>
        confidence switch
        {
            RecommendationConfidence.High => 3,
            RecommendationConfidence.Medium => 2,
            RecommendationConfidence.Low => 1,
            _ => 0
        };

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
            var ranked = recommendations!.Recommended
                .Select((item, apiIndex) => (item, apiIndex))
                .OrderByDescending(entry => RankConfidence(entry.item.Confidence))
                .ThenBy(entry => entry.apiIndex);

            foreach (var (recommendedItem, _) in ranked)
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
