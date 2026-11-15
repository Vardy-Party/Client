using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Playback try-order: crowd-recommended streams first (high confidence
/// before low), then remaining MP (V2) rows, then remaining FB rows - each
/// group in catalog order. Few streams are ever recommended, and most FB rows
/// are dead and take ~20s each to fail (two in flight; only /mp is
/// single-flight), so a catalog-order sweep buries the MP page (regression
/// from e5444f7, #152). An MP row still in /mp never holds first play
/// (StreamResolutionOrchestrator). The control panel badges a stream as
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
        Func<int, StreamModel> getStream,
        int sessionSalt)
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

        if (ordered.Count == 0)
        {
            // Empty or unmatched crowd data: MP first, then session-spread FB.
            return StreamRecommendationPolicy.SpreadDiscoveryOrder(totalStreams, getStream, sessionSalt);
        }

        // Recommended first, then MP, then FB - catalog order within each group.
        ordered.AddRange(remainder.Where(i => StreamRecommendationPolicy.IsMpLike(getStream(i))));
        ordered.AddRange(remainder.Where(i => !StreamRecommendationPolicy.IsMpLike(getStream(i))));
        return ordered;
    }

    public static List<int> Build(
        RecommendationResponse? recommendations,
        int totalStreams,
        Func<string, string?, int> resolveIndex,
        Func<int, StreamModel> getStream) =>
        Build(recommendations, totalStreams, resolveIndex, getStream, sessionSalt: 0);
}
