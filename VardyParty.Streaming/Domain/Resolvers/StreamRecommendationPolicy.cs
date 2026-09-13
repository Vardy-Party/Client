using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

/// <summary>
/// Living recommendation business rules: empty-rec discovery spread, MP chip
/// hoist from crowd keys, stale snapshot detection, and preferred-next selection.
/// </summary>
public static class StreamRecommendationPolicy
{
    public static readonly TimeSpan SoftRefreshInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HardRefreshMaxAge = TimeSpan.FromSeconds(45);

    /// <summary>
    /// When crowd recommendations name MP chips that the slim catalog has not
    /// expanded yet, synthesize labeled candidates so cold-start can target them.
    /// </summary>
    public static List<StreamModel> ApplyRecommendedChips(
        IEnumerable<StreamModel> catalogStreams,
        RecommendationResponse? recommendations)
    {
        var streams = catalogStreams.ToList();
        if (recommendations?.Recommended is not { Count: > 0 })
        {
            return streams;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in streams)
        {
            existing.Add(StreamHealthIdentity.BuildStreamKey(
                stream.Url,
                StreamHealthIdentity.GetStreamName(stream)));
        }

        var byUrl = streams
            .GroupBy(s => StreamHealthIdentity.NormalizeStreamUrl(s.Url), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in recommendations.Recommended)
        {
            if (string.IsNullOrWhiteSpace(item.Url) || string.IsNullOrWhiteSpace(item.StreamName))
            {
                continue;
            }

            var chip = item.StreamName.Trim();
            var key = StreamHealthIdentity.BuildStreamKey(item.Url, chip);
            if (!existing.Add(key))
            {
                continue;
            }

            var normalized = StreamHealthIdentity.NormalizeStreamUrl(item.Url);
            if (!byUrl.TryGetValue(normalized, out var template)
                && !byUrl.TryGetValue(item.Url.Trim(), out template))
            {
                // No catalog page for this recommendation — skip rather than invent FB/MP identity.
                continue;
            }

            if (!IsMpLike(template))
            {
                continue;
            }

            streams.Add(CloneMpChip(template, chip));
        }

        // Drop unlabeled slim MP page rows when at least one chip candidate exists for that URL.
        var urlsWithChips = streams
            .Where(s => IsMpLike(s) && !string.IsNullOrWhiteSpace(StreamHealthIdentity.GetStreamName(s)))
            .Select(s => StreamHealthIdentity.NormalizeStreamUrl(s.Url))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return streams
            .Where(s =>
            {
                if (!IsMpLike(s)) return true;
                if (!string.IsNullOrWhiteSpace(StreamHealthIdentity.GetStreamName(s))) return true;
                return !urlsWithChips.Contains(StreamHealthIdentity.NormalizeStreamUrl(s.Url));
            })
            .ToList();
    }

    /// <summary>
    /// When there are no recommendations, keep FB before MP but rotate within
    /// each source bucket by <paramref name="sessionSalt"/> so concurrent
    /// joiners spread discovery and both benefit once working reports land.
    /// </summary>
    public static List<int> SpreadDiscoveryOrder(
        int totalStreams,
        Func<int, StreamModel> getStream,
        int sessionSalt)
    {
        if (totalStreams <= 0)
        {
            return [];
        }

        var fb = new List<int>();
        var other = new List<int>();
        var mp = new List<int>();
        for (var i = 0; i < totalStreams; i++)
        {
            switch (getStream(i).ResolveCatalogSource())
            {
                case "fb":
                    fb.Add(i);
                    break;
                case "mp":
                    mp.Add(i);
                    break;
                default:
                    other.Add(i);
                    break;
            }
        }

        RotateInPlace(fb, sessionSalt);
        RotateInPlace(other, sessionSalt + 17);
        RotateInPlace(mp, sessionSalt + 31);

        var ordered = new List<int>(totalStreams);
        ordered.AddRange(fb);
        ordered.AddRange(other);
        ordered.AddRange(mp);
        return ordered;
    }

    public static bool IsStale(RecommendationResponse? recommendations, DateTimeOffset nowUtc, TimeSpan maxAge)
    {
        if (recommendations?.GeneratedAt is long generatedAt and > 0)
        {
            var ageMs = nowUtc.ToUnixTimeMilliseconds() - generatedAt;
            return ageMs < 0 || ageMs > maxAge.TotalMilliseconds;
        }

        // Missing generatedAt → treat as stale so living clients refresh.
        return true;
    }

    /// <summary>
    /// Prefer the highest-confidence recommended peer that is not the current
    /// stream and is present in the healthy pool.
    /// </summary>
    /// <remarks>
    /// Only use this on wraparound (after a full cycle). Using it on every Next
    /// traps the user bouncing between the top recommended peers while other
    /// healthy streams are never reached — Windows/TV showed 6 streams but
    /// only cycled 2.
    /// </remarks>
    public static EnrichedStream? PickPreferredNext(
        RecommendationResponse? recommendations,
        IReadOnlyList<EnrichedStream> healthy,
        EnrichedStream? current)
    {
        if (recommendations?.Recommended is not { Count: > 0 } || healthy.Count == 0)
        {
            return null;
        }

        var currentKey = current == null
            ? null
            : StreamHealthIdentity.BuildStreamKey(
                current.Stream.Url,
                StreamHealthIdentity.GetStreamName(current.Stream));

        var ranked = recommendations.Recommended
            .Select((item, apiIndex) => (item, apiIndex))
            .OrderByDescending(entry => StreamTestOrderPolicy.RankConfidence(entry.item.Confidence))
            .ThenBy(entry => entry.apiIndex);

        foreach (var (item, _) in ranked)
        {
            if (string.IsNullOrWhiteSpace(item.Url))
            {
                continue;
            }

            var match = healthy.FirstOrDefault(candidate =>
                StreamHealthIdentity.MatchesRecommendation(
                    candidate.Stream,
                    item.Url,
                    item.StreamName));
            if (match == null)
            {
                continue;
            }

            var matchKey = StreamHealthIdentity.BuildStreamKey(
                match.Stream.Url,
                StreamHealthIdentity.GetStreamName(match.Stream));
            if (currentKey != null
                && string.Equals(currentKey, matchKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return match;
        }

        return null;
    }

    /// <summary>
    /// Hardware/UI Next must walk the full healthy pool in order. Recommendation
    /// preference is reserved for wraparound reordering.
    /// </summary>
    public static bool ShouldPreferRecommendedPeerOnNext(bool wouldWrapAround) =>
        wouldWrapAround;

    public static bool WouldWrapAround(int currentIndex, int healthyCount) =>
        healthyCount > 0 && currentIndex >= 0 && ((currentIndex + 1) % healthyCount) == 0;

    private static void RotateInPlace(List<int> indexes, int salt)
    {
        if (indexes.Count <= 1)
        {
            return;
        }

        var offset = Math.Abs(salt) % indexes.Count;
        if (offset == 0)
        {
            return;
        }

        var rotated = indexes.Skip(offset).Concat(indexes.Take(offset)).ToList();
        indexes.Clear();
        indexes.AddRange(rotated);
    }

    private static bool IsMpLike(StreamModel stream) =>
        stream.RequiresV2StreamSelection
        || string.Equals(stream.ResolveCatalogSource(), "mp", StringComparison.OrdinalIgnoreCase);

    private static StreamModel CloneMpChip(StreamModel source, string chip) =>
        new()
        {
            Url = source.Url,
            Channel = chip,
            PlayerStream = chip,
            Source = string.IsNullOrWhiteSpace(source.Source) ? "mp" : source.Source,
            ResolutionStrategy = string.IsNullOrWhiteSpace(source.ResolutionStrategy)
                ? "v2"
                : source.ResolutionStrategy,
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
