using VardyParty.Kernel;

namespace VardyParty.Streaming;

public interface IStreamSelectionCoordinator
{
    IObservable<StreamSelectionProgress> ProgressUpdated { get; }

    Task InitializeAsync(Game game, CancellationToken cancellationToken = default);

    StreamSelectionCandidate? GetNextCandidate();

    IReadOnlyList<StreamSelectionCandidate> GetOrderedCandidates();

    void ReportTestResult(int streamIndex, bool isWorking);

    void PauseTesting();

    void ResumeTesting();

    IReadOnlyList<int> GetUntestedIndexes();

    bool TryGetStreamIndex(string? streamUrlOrReferer, out int index);

    void Reset();
}
