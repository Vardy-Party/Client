using Microsoft.Extensions.Logging;
using VardyParty.Models;

namespace VardyParty.Resolvers;

public class StreamDeduplicator(ILogger<StreamDeduplicator> logger) : IStreamDeduplicator
{
    private static readonly Dictionary<string, int> ReputationRanking = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Very Good", 5 },
        { "Good", 4 },
        { "OK", 3 },
        { "Poor", 2 },
        { "Bad", 1 },
        { "", 0 } // No reputation = lowest priority
    };

    public List<Models.Stream> DeduplicateStreams(List<Models.Stream> streams)
    {
        if (streams == null || streams.Count == 0)
        {
            return new List<Models.Stream>();
        }

        logger.LogInformation("[Dedup] Deduplicating {Count} streams", streams.Count);

        var groupedByBaseUrl = new Dictionary<string, List<Models.Stream>>(StringComparer.OrdinalIgnoreCase);

        // Group streams by their base URL (and player label for v2 multi-stream pages)
        foreach (var stream in streams)
        {
            var dedupKey = GetDedupKey(stream);
            
            if (!groupedByBaseUrl.ContainsKey(dedupKey))
            {
                groupedByBaseUrl[dedupKey] = new List<Models.Stream>();
            }
            
            groupedByBaseUrl[dedupKey].Add(stream);
        }

        // Select best stream from each group
        var deduplicated = new List<Models.Stream>();
        foreach (var group in groupedByBaseUrl.Values)
        {
            var best = SelectBestStream(group);
            deduplicated.Add(best);
        }

        var removedCount = streams.Count - deduplicated.Count;
        if (removedCount > 0)
        {
            logger.LogInformation("[Dedup] Removed {Removed} duplicate streams, {Remaining} unique remain", 
                removedCount, deduplicated.Count);
        }

        return deduplicated;
    }

    public string ExtractBaseUrl(string url) => StreamUrlNormalizer.NormalizeForDedup(url);

    private static string GetDedupKey(Models.Stream stream)
    {
        var baseUrl = StreamUrlNormalizer.NormalizeForDedup(stream.Url);
        if (!stream.RequiresV2StreamSelection)
        {
            return baseUrl;
        }

        var playerLabel = string.IsNullOrWhiteSpace(stream.PlayerStream)
            ? stream.Channel
            : stream.PlayerStream;

        return string.IsNullOrWhiteSpace(playerLabel)
            ? baseUrl
            : $"{baseUrl}\0{playerLabel.Trim()}";
    }

    private Models.Stream SelectBestStream(List<Models.Stream> group)
    {
        if (group.Count == 1)
            return group[0];

        // Sort by reputation score (highest first), then FB before MP, then by channel name
        var sorted = group
            .OrderByDescending(s => GetReputationScore(s.Reputation))
            .ThenBy(s => StreamCatalogSourceOrderer.GetCatalogSourcePriority(s))
            .ThenBy(s => s.Channel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var best = sorted[0];
        
        if (sorted.Count > 1)
        {
            logger.LogInformation("[Dedup] Selected '{Channel}' (rep: {Reputation}) over {Count} duplicate(s) for URL base", 
                best.Channel, best.Reputation, sorted.Count - 1);
        }

        return best;
    }

    private int GetReputationScore(string? reputation)
    {
        if (string.IsNullOrEmpty(reputation))
            return 0;

        return ReputationRanking.TryGetValue(reputation, out var score) ? score : 0;
    }
}
