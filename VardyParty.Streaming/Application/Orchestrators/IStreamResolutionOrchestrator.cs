using VardyParty.Kernel;
using VardyParty.Ports;

namespace VardyParty.Streaming;

public interface IStreamResolutionOrchestrator
{
    IObservable<StreamResolutionProgress> ProgressUpdated { get; }

    Task<StreamResolutionOutcome> StartAsync(
        Game game,
        IPlaybackLauncher launcher,
        CancellationToken cancellationToken = default);

    Task ReportCurrentStreamAsBadAsync(string? reason = null, CancellationToken cancellationToken = default);

    void Reset();
}
