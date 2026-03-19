using VardyParty.Models;

namespace VardyParty.Orchestrators;

public interface IStreamResolutionOrchestrator
{
    IObservable<StreamResolutionProgress> ProgressUpdated { get; }

    Task<StreamResolutionOutcome> StartAsync(Game game, CancellationToken cancellationToken = default);

    Task ReportCurrentStreamAsBadAsync(string? reason = null, CancellationToken cancellationToken = default);

    void Reset();
}
