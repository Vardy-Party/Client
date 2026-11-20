using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VardyParty.Kernel;

namespace VardyParty.Streaming.Tests;

/// <summary>
/// A resolver script for orchestrator tests: <see cref="Yield"/> hands rows to the
/// orchestrator, and <see cref="DrainedAsync"/> completes once it has taken (and pooled)
/// every one of them. Plug <see cref="DrainedAsync"/> into
/// <c>StreamResolutionOrchestrator.PartnerLookoutAsync</c> so a fresh start picks its
/// partner from a full pool instead of racing a wall-clock lookout. One instance per test.
/// </summary>
internal sealed class ScriptedResolver
{
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once every row from <see cref="Yield"/> has been taken.</summary>
    public Task DrainedAsync(CancellationToken cancellationToken) =>
        _drained.Task.WaitAsync(cancellationToken);

    public async IAsyncEnumerable<EnrichedStream> Yield(params EnrichedStream[] streams)
    {
        foreach (var stream in streams)
            yield return stream;
        // Runs when the orchestrator asks for the row after the last one: its
        // Accept/JoinPool for every row above has already run synchronously.
        _drained.TrySetResult();
        await Task.CompletedTask;
    }
}