namespace VardyParty.Kernel;

/// <summary>One leg of a race once it has finished.</summary>
public sealed record RaceLegResult<T>(EnrichedStream Row, RaceLegState State, T? Value, string? Error)
    where T : class;

/// <summary>What <see cref="PlaybackUrlRacer.RaceAsync{T}"/> decided.</summary>
public sealed record PlaybackRaceResult<T>(
    EnrichedStream? Winner,
    T? WinnerValue,
    RaceLegResult<T>? Recommended,
    RaceLegResult<T>? Partner,
    bool PlayReadyRow)
    where T : class
{
    /// <summary>Finished legs that are a verdict on the stream (no playlist, or audio-only).</summary>
    public IEnumerable<RaceLegResult<T>> StreamVerdicts
        => new[] { Recommended, Partner }
            .Where(leg => leg is { State: RaceLegState.Empty or RaceLegState.AudioOnly })
            .Select(leg => leg!);

    /// <summary>True when a finished leg got no answer from the service.</summary>
    public bool AnyInfrastructure
        => Recommended is { State: RaceLegState.Infrastructure }
            || Partner is { State: RaceLegState.Infrastructure };

    /// <summary>Error text of the first leg the service could not answer.</summary>
    public string? InfrastructureError
        => (Recommended is { State: RaceLegState.Infrastructure } ? Recommended : Partner)?.Error;
}

/// <summary>
/// Runs two fresh <c>/play</c> calls and applies <see cref="PlaybackRacePolicy.Decide"/>.
/// A leg still running at the decision keeps running; <paramref name="onFinished"/> runs
/// as each leg completes (including after the decision) so a losing URL can be kept warm.
/// </summary>
public static class PlaybackUrlRacer
{
    public static async Task<PlaybackRaceResult<T>> RaceAsync<T>(
        EnrichedStream recommended,
        EnrichedStream partner,
        Func<EnrichedStream, CancellationToken, Task<T?>> resolve,
        Func<T, CancellationToken, Task<RaceLegState>> classify,
        Action<RaceLegResult<T>>? onFinished,
        Func<bool>? otherRowCanPlayNow,
        CancellationToken cancellationToken,
        TimeSpan? grace = null,
        TimeProvider? timeProvider = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(recommended);
        ArgumentNullException.ThrowIfNull(partner);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(classify);
        var time = timeProvider ?? TimeProvider.System;
        var window = grace ?? PlaybackRacePolicy.RecommendedGrace;

        // Both /play calls must be issued before Decide can return PlayReadyRow.
        // Otherwise an empty recommended leg can win immediately while the partner
        // is still queued after its Yield — the losing URL would never warm.
        var recommendedIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var partnerIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recommendedLeg = RunLegAsync(
            recommended, resolve, classify, onFinished, recommendedIssued, cancellationToken);
        var partnerLeg = RunLegAsync(
            partner, resolve, classify, onFinished, partnerIssued, cancellationToken);
        await Task.WhenAll(recommendedIssued.Task, partnerIssued.Task).ConfigureAwait(false);
        Task? graceTask = null;

        while (true)
        {
            var r = StateOf(recommendedLeg);
            var p = StateOf(partnerLeg);
            if (graceTask == null && window >= TimeSpan.Zero
                && ((p == RaceLegState.Video && r == RaceLegState.Pending)
                    || (r == RaceLegState.Unverified && p == RaceLegState.Pending)))
            {
                graceTask = window > TimeSpan.Zero
                    ? Task.Delay(window, time, cancellationToken)
                    : Task.CompletedTask;
            }

            var graceElapsed = graceTask?.IsCompleted == true;
            var decision = PlaybackRacePolicy.Decide(
                r, p, graceElapsed, otherRowCanPlayNow?.Invoke() == true);
            switch (decision)
            {
                case RaceDecision.Recommended:
                    return Result(recommended, recommendedLeg, partnerLeg);
                case RaceDecision.Partner:
                    return Result(partner, partnerLeg, recommendedLeg, partnerWon: true);
                case RaceDecision.PlayReadyRow:
                    return new PlaybackRaceResult<T>(
                        null, null, Finished(recommendedLeg), Finished(partnerLeg), PlayReadyRow: true);
                case RaceDecision.NoWinner:
                    return new PlaybackRaceResult<T>(
                        null, null, Finished(recommendedLeg), Finished(partnerLeg), PlayReadyRow: false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var waits = new List<Task>(4);
            // Wait on what this decision saw as still running: the r, p and graceElapsed
            // snapshot above, not a second IsCompleted read. A leg (or the grace delay)
            // that finished after the snapshot then completes WhenAny at once and the next
            // pass decides on it. Reading IsCompleted again here could drop a leg that had
            // just finished, so the race either ended with no winner or parked on the
            // other leg's /play until it answered (or forever).
            if (r == RaceLegState.Pending)
                waits.Add(recommendedLeg);
            if (p == RaceLegState.Pending)
                waits.Add(partnerLeg);
            if (graceTask is not null && !graceElapsed)
                waits.Add(graceTask);
            // Recommended already failed while the partner /play is still out: a cached
            // pool row may join after this Decide. Re-poll otherRowCanPlayNow on a short
            // tick instead of parking only on the partner (which can hang the finding dialog).
            if (p == RaceLegState.Pending
                && r is RaceLegState.Empty or RaceLegState.AudioOnly or RaceLegState.Infrastructure)
            {
                waits.Add(Task.Delay(TimeSpan.FromMilliseconds(25), time, cancellationToken));
            }

            if (waits.Count == 0)
            {
                return new PlaybackRaceResult<T>(
                    null, null, Finished(recommendedLeg), Finished(partnerLeg), PlayReadyRow: false);
            }

            await Task.WhenAny(waits).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        PlaybackRaceResult<T> Result(
            EnrichedStream winner,
            Task<RaceLegResult<T>> winnerLeg,
            Task<RaceLegResult<T>> loserLeg,
            bool partnerWon = false)
        {
            var won = winnerLeg.Result;
            var lost = Finished(loserLeg);
            return partnerWon
                ? new PlaybackRaceResult<T>(winner, won.Value, lost, won, false)
                : new PlaybackRaceResult<T>(winner, won.Value, won, lost, false);
        }
    }

    private static async Task<RaceLegResult<T>> RunLegAsync<T>(
        EnrichedStream row,
        Func<EnrichedStream, CancellationToken, Task<T?>> resolve,
        Func<T, CancellationToken, Task<RaceLegState>> classify,
        Action<RaceLegResult<T>>? onFinished,
        TaskCompletionSource issued,
        CancellationToken cancellationToken)
        where T : class
    {
        // Yield first so both legs schedule before either resolve runs, then mark
        // issued as soon as resolve has been invoked (before awaiting its result).
        await Task.Yield();
        RaceLegResult<T> result;
        try
        {
            Task<T?> pending;
            try
            {
                pending = resolve(row, cancellationToken);
            }
            finally
            {
                issued.TrySetResult();
            }

            var value = await pending.ConfigureAwait(false);
            if (value is null)
            {
                result = new RaceLegResult<T>(row, RaceLegState.Empty, null, null);
            }
            else
            {
                var state = await classify(value, cancellationToken).ConfigureAwait(false);
                if (state is not (RaceLegState.Video or RaceLegState.Unverified or RaceLegState.AudioOnly))
                    state = RaceLegState.Empty;
                result = new RaceLegResult<T>(row, state, value, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            issued.TrySetResult();
            throw;
        }
        catch (Exception ex)
        {
            // Infrastructure, or anything else that is not an answer about the stream.
            issued.TrySetResult();
            result = new RaceLegResult<T>(row, RaceLegState.Infrastructure, null, ex.Message);
        }

        try
        {
            onFinished?.Invoke(result);
        }
        catch
        {
            // Keeping a loser warm must not fail the race.
        }

        return result;
    }

    private static RaceLegState StateOf<T>(Task<RaceLegResult<T>> leg)
        where T : class
        => leg.IsCompletedSuccessfully ? leg.Result.State : RaceLegState.Pending;

    private static RaceLegResult<T>? Finished<T>(Task<RaceLegResult<T>> leg)
        where T : class
        => leg.IsCompletedSuccessfully ? leg.Result : null;
}
