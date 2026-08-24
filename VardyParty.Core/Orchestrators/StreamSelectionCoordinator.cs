using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using VardyParty.Health;
using VardyParty.Models;
using VardyParty.Resolvers;
using VardyParty.Services;
using StreamModel = VardyParty.Models.Stream;

namespace VardyParty.Orchestrators;

public class StreamSelectionCoordinator(
    IApiService apiService,
    IStreamHealthService streamHealthService,
    ILogger<StreamSelectionCoordinator> logger) : IStreamSelectionCoordinator
{
    private readonly BehaviorSubject<StreamSelectionProgress> _progressSubject =
        new(new StreamSelectionProgress());

    private readonly List<StreamSelectionCandidate> _candidates = new();
    private readonly Queue<int> _pendingIndexes = new();
    private readonly HashSet<int> _testedIndexes = new();
    private readonly HashSet<int> _workingIndexes = new();
    private readonly Dictionary<string, int> _streamIndexByKey = new(StringComparer.OrdinalIgnoreCase);

    private bool _paused;
    private int _totalStreams;

    public IObservable<StreamSelectionProgress> ProgressUpdated => _progressSubject;

    public async Task InitializeAsync(Game game, CancellationToken cancellationToken = default)
    {
        Reset();

        var streamsResponse = await apiService.GetStreamsAsync(game.ApiLeague, game.Home, game.Away);
        if (streamsResponse?.Streams == null || streamsResponse.Streams.Count == 0)
        {
            PublishProgress(status: "No streams found", isPaused: true);
            return;
        }

        var expandedStreams = StreamCatalogSourceOrderer.OrderFbBeforeMp(
            V2StreamExpander.Expand(streamsResponse.Streams));
        _totalStreams = expandedStreams.Count;
        BuildCandidates(expandedStreams);

        List<int> testOrder = new();
        try
        {
            var recommendations = await streamHealthService.GetRecommendationsAsync(
                game.ApiLeague,
                game.Home,
                game.Away,
                cancellationToken);

            if (recommendations != null)
            {
                logger.LogInformation("[StreamSelection] Recommendations received. Confidence={Confidence}, Recommended count={Count}",
                    recommendations.Confidence,
                    recommendations.Recommended?.Count ?? 0);
            }
            else
            {
                logger.LogInformation("[StreamSelection] No recommendations returned");
            }

            testOrder = BuildTestOrder(recommendations, _totalStreams);

            if (testOrder.Count > 0)
            {
                logger.LogInformation("[StreamSelection] Using recommendation-based test order: {Order}",
                    string.Join(",", testOrder));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StreamSelection] Failed to fetch recommendations, defaulting to original order");
        }

        if (testOrder.Count == 0)
        {
            testOrder = Enumerable.Range(0, _totalStreams).ToList();
        }

        foreach (var index in testOrder)
        {
            _pendingIndexes.Enqueue(index);
        }

        PublishProgress(status: "Ready to test streams", isPaused: false);
    }

    public StreamSelectionCandidate? GetNextCandidate()
    {
        if (_paused) return null;
        while (_pendingIndexes.Count > 0)
        {
            var nextIndex = _pendingIndexes.Peek();
            if (_testedIndexes.Contains(nextIndex))
            {
                _pendingIndexes.Dequeue();
                continue;
            }

            return _candidates.FirstOrDefault(c => c.Index == nextIndex);
        }

        return null;
    }

    public IReadOnlyList<StreamSelectionCandidate> GetOrderedCandidates()
    {
        var ordered = new List<StreamSelectionCandidate>();
        foreach (var index in _pendingIndexes)
        {
            var candidate = _candidates.FirstOrDefault(c => c.Index == index);
            if (candidate != null)
            {
                ordered.Add(candidate);
            }
        }

        return ordered;
    }

    public void ReportTestResult(int streamIndex, bool isWorking)
    {
        if (!_testedIndexes.Add(streamIndex)) return;

        if (isWorking)
        {
            _workingIndexes.Add(streamIndex);
        }

        var workingCount = _workingIndexes.Count;
        var testedCount = _testedIndexes.Count;

        PublishProgress(isPaused: _paused, streamsTested: testedCount, workingStreams: workingCount);
    }

    public void PauseTesting()
    {
        _paused = true;
        PublishProgress(isPaused: true, status: "Testing paused");
    }

    public void ResumeTesting()
    {
        _paused = false;
        PublishProgress(isPaused: false, status: "Testing resumed");
    }

    public IReadOnlyList<int> GetUntestedIndexes()
    {
        return _pendingIndexes
            .Where(index => !_testedIndexes.Contains(index))
            .ToList();
    }

    public bool TryGetStreamIndex(string? streamUrlOrReferer, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(streamUrlOrReferer)) return false;

        var normalized = StreamHealthIdentity.NormalizeStreamUrl(streamUrlOrReferer);
        if (_streamIndexByKey.TryGetValue(normalized, out index)) return true;

        return _streamIndexByKey.TryGetValue(streamUrlOrReferer, out index);
    }

    public void Reset()
    {
        _paused = false;
        _totalStreams = 0;
        _candidates.Clear();
        _pendingIndexes.Clear();
        _testedIndexes.Clear();
        _workingIndexes.Clear();
        _streamIndexByKey.Clear();
        PublishProgress();
    }

    private void BuildCandidates(List<StreamModel> streams)
    {
        for (var i = 0; i < streams.Count; i++)
        {
            var stream = streams[i];
            var normalized = StreamHealthIdentity.NormalizeStreamUrl(stream.Url);
            var streamKey = StreamHealthIdentity.BuildStreamKey(stream.Url, StreamHealthIdentity.GetStreamName(stream));
            var candidate = new StreamSelectionCandidate
            {
                Index = i,
                Stream = stream,
                NormalizedUrl = normalized
            };

            _candidates.Add(candidate);
            if (!string.IsNullOrWhiteSpace(streamKey))
            {
                _streamIndexByKey[streamKey] = i;
            }

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _streamIndexByKey[normalized] = i;
            }

            if (!string.IsNullOrWhiteSpace(stream.Url))
            {
                _streamIndexByKey[stream.Url] = i;
            }
        }
    }

    private List<int> BuildTestOrder(RecommendationResponse? recommendations, int totalStreams)
    {
        if (recommendations == null ||
            !string.Equals(recommendations.Confidence, "high", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(recommendations.Confidence, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Range(0, totalStreams).ToList();
        }

        var ordered = new List<int>();
        var seen = new HashSet<int>();

        foreach (var recommendedItem in recommendations.Recommended)
        {
            var index = ResolveRecommendationIndex(recommendedItem.Url, recommendedItem.StreamName);
            if (index < 0 || index >= totalStreams || !seen.Add(index))
            {
                continue;
            }

            ordered.Add(index);
        }

        for (var i = 0; i < totalStreams; i++)
        {
            if (!seen.Contains(i))
            {
                ordered.Add(i);
            }
        }

        return StreamCatalogSourceOrderer.OrderIndexesFbBeforeMp(
            ordered,
            index => _candidates.First(c => c.Index == index).Stream);
    }

    private int ResolveRecommendationIndex(string recommendedUrl, string? recommendedStreamName)
    {
        if (!string.IsNullOrWhiteSpace(recommendedStreamName))
        {
            var compositeKey = StreamHealthIdentity.BuildStreamKey(recommendedUrl, recommendedStreamName);
            if (_streamIndexByKey.TryGetValue(compositeKey, out var compositeIndex))
            {
                return compositeIndex;
            }

            var normalizedComposite = StreamHealthIdentity.BuildStreamKey(
                StreamHealthIdentity.NormalizeStreamUrl(recommendedUrl),
                recommendedStreamName);
            if (_streamIndexByKey.TryGetValue(normalizedComposite, out compositeIndex))
            {
                return compositeIndex;
            }
        }

        if (_streamIndexByKey.TryGetValue(recommendedUrl, out var index))
        {
            return index;
        }

        var normalized = StreamHealthIdentity.NormalizeStreamUrl(recommendedUrl);
        return !string.IsNullOrWhiteSpace(normalized) && _streamIndexByKey.TryGetValue(normalized, out index)
            ? index
            : -1;
    }

    private void PublishProgress(
        string? status = null,
        bool? isPaused = null,
        int? streamsTested = null,
        int? workingStreams = null)
    {
        var current = _progressSubject.Value;
        var next = new StreamSelectionProgress
        {
            TotalStreams = _totalStreams,
            StreamsTested = streamsTested ?? current.StreamsTested,
            WorkingStreams = workingStreams ?? current.WorkingStreams,
            IsPaused = isPaused ?? current.IsPaused,
            Status = status ?? current.Status
        };

        _progressSubject.OnNext(next);
    }
}
