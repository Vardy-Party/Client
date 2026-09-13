using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using VardyParty.Kernel;
using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Streaming;

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
    private readonly object _gate = new();
    private readonly SemaphoreSlim _initializeLock = new(1, 1);

    private bool _paused;
    private int _totalStreams;
    private int _discoverySalt = Random.Shared.Next();
    private RecommendationResponse? _latestRecommendations;

    public IObservable<StreamSelectionProgress> ProgressUpdated => _progressSubject;

    public RecommendationResponse? LatestRecommendations
    {
        get
        {
            lock (_gate) return _latestRecommendations;
        }
    }

    public void RememberRecommendations(RecommendationResponse? recommendations)
    {
        lock (_gate)
        {
            _latestRecommendations = recommendations;
        }
    }

    public int DiscoverySalt
    {
        get
        {
            lock (_gate) return _discoverySalt;
        }
    }

    public async Task InitializeAsync(Game game, CancellationToken cancellationToken = default)
    {
        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            Reset();

            var streamsResponse = await apiService.GetStreamsAsync(game.ApiLeague, game.Home, game.Away);
            if (streamsResponse?.Streams == null || streamsResponse.Streams.Count == 0)
            {
                PublishProgress(status: "No streams found", isPaused: true);
                return;
            }

            RecommendationResponse? recommendations = null;
            try
            {
                recommendations = await streamHealthService.GetRecommendationsAsync(
                    game.ApiLeague,
                    game.Home,
                    game.Away,
                    cancellationToken);

                if (recommendations != null)
                {
                    logger.LogInformation(
                        "[StreamSelection] Recommendations received: {Summary}",
                        RecommendationLogFormatter.Format(recommendations));
                }
                else
                {
                    logger.LogInformation("[StreamSelection] No recommendations returned");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[StreamSelection] Failed to fetch recommendations, defaulting to discovery spread");
            }

            var expandedStreams = StreamCatalogSourceOrderer.OrderFbBeforeMp(
                StreamRecommendationPolicy.ApplyRecommendedChips(
                    V2StreamExpander.Expand(streamsResponse.Streams),
                    recommendations));

            List<int> testOrder;
            lock (_gate)
            {
                _latestRecommendations = recommendations;
                _totalStreams = expandedStreams.Count;
                BuildCandidates(expandedStreams);
                testOrder = BuildTestOrder(recommendations, _totalStreams);
            }

            string LabelForIndex(int index)
            {
                lock (_gate)
                {
                    var candidate = _candidates.FirstOrDefault(c => c.Index == index);
                    if (candidate is null)
                        return "?";
                    var name = StreamHealthIdentity.GetStreamName(candidate.Stream);
                    return string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
                }
            }

            if (StreamTestOrderPolicy.ShouldPreferRecommendations(recommendations))
            {
                logger.LogInformation(
                    "[StreamSelection] Using recommendation-based test order. OverallConfidence={Confidence}, Order={Order}",
                    recommendations?.Confidence,
                    RecommendationLogFormatter.FormatTestOrder(testOrder, LabelForIndex));
            }
            else
            {
                logger.LogInformation(
                    "[StreamSelection] Using session-spread discovery order (salt={Salt}): {Order}",
                    _discoverySalt,
                    RecommendationLogFormatter.FormatTestOrder(testOrder, LabelForIndex));
            }

            lock (_gate)
            {
                if (testOrder.Count == 0)
                {
                    testOrder = Enumerable.Range(0, _totalStreams).ToList();
                }

                foreach (var index in testOrder)
                {
                    _pendingIndexes.Enqueue(index);
                }
            }

            PublishProgress(status: "Ready to test streams", isPaused: false);
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public StreamSelectionCandidate? GetNextCandidate()
    {
        lock (_gate)
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
    }

    public IReadOnlyList<StreamSelectionCandidate> GetOrderedCandidates()
    {
        lock (_gate)
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
    }

    public void ReportTestResult(int streamIndex, bool isWorking)
    {
        int workingCount;
        int testedCount;
        bool paused;
        lock (_gate)
        {
            if (!_testedIndexes.Add(streamIndex)) return;

            if (isWorking)
            {
                _workingIndexes.Add(streamIndex);
            }

            workingCount = _workingIndexes.Count;
            testedCount = _testedIndexes.Count;
            paused = _paused;
        }

        PublishProgress(isPaused: paused, streamsTested: testedCount, workingStreams: workingCount);
    }

    public void PauseTesting()
    {
        lock (_gate)
        {
            _paused = true;
        }

        PublishProgress(isPaused: true, status: "Testing paused");
    }

    public void ResumeTesting()
    {
        lock (_gate)
        {
            _paused = false;
        }

        PublishProgress(isPaused: false, status: "Testing resumed");
    }

    public IReadOnlyList<int> GetUntestedIndexes()
    {
        lock (_gate)
        {
            return _pendingIndexes
                .Where(index => !_testedIndexes.Contains(index))
                .ToList();
        }
    }

    public bool TryGetStreamIndex(string? streamUrlOrReferer, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(streamUrlOrReferer)) return false;

        var normalized = StreamHealthIdentity.NormalizeStreamUrl(streamUrlOrReferer);
        lock (_gate)
        {
            if (_streamIndexByKey.TryGetValue(normalized, out index)) return true;

            return _streamIndexByKey.TryGetValue(streamUrlOrReferer, out index);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _paused = false;
            _totalStreams = 0;
            _candidates.Clear();
            _pendingIndexes.Clear();
            _testedIndexes.Clear();
            _workingIndexes.Clear();
            _streamIndexByKey.Clear();
            _latestRecommendations = null;
            _discoverySalt = Random.Shared.Next();
        }

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

            if (!string.IsNullOrWhiteSpace(normalized)
                && string.IsNullOrWhiteSpace(StreamHealthIdentity.GetStreamName(stream)))
            {
                // Only index bare URLs for unlabeled candidates so chip keys win for MP.
                _streamIndexByKey[normalized] = i;
            }

            if (!string.IsNullOrWhiteSpace(stream.Url)
                && string.IsNullOrWhiteSpace(StreamHealthIdentity.GetStreamName(stream)))
            {
                _streamIndexByKey[stream.Url] = i;
            }
        }
    }

    private List<int> BuildTestOrder(RecommendationResponse? recommendations, int totalStreams)
    {
        return StreamTestOrderPolicy.Build(
            recommendations,
            totalStreams,
            ResolveRecommendationIndex,
            index => _candidates.First(c => c.Index == index).Stream,
            _discoverySalt);
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

            // Chip recommendations must not fall back to an unlabeled page URL row.
            foreach (var candidate in _candidates)
            {
                if (StreamHealthIdentity.MatchesRecommendation(
                        candidate.Stream, recommendedUrl, recommendedStreamName))
                {
                    return candidate.Index;
                }
            }

            return -1;
        }

        if (_streamIndexByKey.TryGetValue(recommendedUrl, out var index))
        {
            return index;
        }

        var normalized = StreamHealthIdentity.NormalizeStreamUrl(recommendedUrl);
        if (!string.IsNullOrWhiteSpace(normalized) && _streamIndexByKey.TryGetValue(normalized, out index))
        {
            return index;
        }

        foreach (var candidate in _candidates)
        {
            if (StreamHealthIdentity.MatchesRecommendation(candidate.Stream, recommendedUrl, recommendedStreamName))
            {
                return candidate.Index;
            }
        }

        return -1;
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
