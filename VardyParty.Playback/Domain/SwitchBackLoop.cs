namespace VardyParty.Playback;

/// <summary>
/// Drives option 2 probes and the switch-back attach. Call from the player host
/// with a fake clock / fetch in tests, or HttpClient + attach in production.
/// </summary>
public static class SwitchBackLoop
{
    public static async Task RunAsync(
        SwitchBackSession session,
        Func<SwitchBackCandidate, CancellationToken, Task<(bool Playable, int StatusCode)>> probe,
        Func<SwitchBackCandidate, string, CancellationToken, Task> switchBack,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(switchBack);
        delay ??= (d, ct) => d <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(d, ct);
        clock ??= () => DateTimeOffset.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!session.IsArmed)
            {
                try
                {
                    await delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            var decision = session.Next(clock());
            switch (decision.Action)
            {
                case SwitchBackAction.Stop:
                    return;
                case SwitchBackAction.Wait:
                    try
                    {
                        await delay(decision.Wait ?? TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    break;
                case SwitchBackAction.Probe:
                    var candidate = session.Candidate!;
                    log?.Invoke($"[switch-back] probing row=\"{candidate.RowName ?? "(unknown)"}\"");
                    var result = await probe(candidate, cancellationToken).ConfigureAwait(false);
                    decision = session.NoteProbe(result.Playable, result.StatusCode, clock());
                    if (decision.Action == SwitchBackAction.SwitchBack)
                        goto switchNow;
                    break;
                case SwitchBackAction.SwitchBack:
                    goto switchNow;
            }

            continue;

        switchNow:
            var target = session.Candidate!;
            var why = decision.Reason ?? "recovered";
            log?.Invoke($"[switch-back] returning to row=\"{target.RowName ?? "(unknown)"}\" ({why})");
            await switchBack(target, why, cancellationToken).ConfigureAwait(false);
            session.Cancel("switched back");
        }
    }
}
