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

        var recommendedLeg = RunLegAsync(recommended, resolve, classify, onFinished, cancellationToken);
        var partnerLeg = RunLegAsync(partner, resolve, classify, onFinished, cancellationToken);
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

            var decision = PlaybackRacePolicy.Decide(
                r, p, graceTask?.IsCompleted == true, otherRowCanPlayNow?.Invoke() == true);
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
            if (!recommendedLeg.IsCompleted)
                waits.Add(recommendedLeg);
            if (!partnerLeg.IsCompleted)
                waits.Add(partnerLeg);
            if (graceTask is { IsCompleted: false })
                waits.Add(graceTask);
            // Recommended already failed while the partner /play is still out: a cached
            // pool row may join after this Decide. Re-poll otherRowCanPlayNow on a short
            // tick instead of parking only on the partner (which can hang the finding dialog).
            if (partnerLeg.IsCompleted == false
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
        CancellationToken cancellationToken)
        where T : class
    {
        // Yield first so both /play calls are issued before either is awaited.
        await Task.Yield();
        RaceLegResult<T> result;
        try
        {
            var value = await resolve(row, cancellationToken).ConfigureAwait(false);
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
            throw;
        }
        catch (Exception ex)
        {
            // Infrastructure, or anything else that is not an answer about the stream.
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
