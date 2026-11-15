namespace VardyParty.Playback;

/// <summary>
/// Drives option 2 probes and the switch-back attach. Call from the player host
/// with a fake clock / probe in tests, or <see cref="ISwitchBackProbe"/> + the UI
/// dispatcher in production (<see cref="SwitchBackRunner.Create"/>).
/// </summary>
/// <remarks>
/// The loop never dies on an exception. A probe that throws (connection refused,
/// DNS, timeout — the usual CDN-unreachable cases) counts as an unhealthy probe
/// and the backoff carries on. A switch-back that throws is logged and cancels
/// that arm. Every result is tied to the arm id it was made for, so a re-arm
/// while a probe or switch is in flight is never cancelled or credited by it.
/// </remarks>
public static class SwitchBackLoop
{
    public static async Task RunAsync(
        SwitchBackSession session,
        Func<SwitchBackCandidate, CancellationToken, Task<SwitchBackProbeResult>> probe,
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
                if (!await DelayAsync(delay, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
                    return;
                continue;
            }

            var decision = session.Next(clock());
            switch (decision.Action)
            {
                case SwitchBackAction.Stop:
                    return;

                case SwitchBackAction.Wait:
                    if (!await DelayAsync(delay, decision.Wait ?? TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
                        return;
                    continue;

                case SwitchBackAction.Probe:
                {
                    var candidate = decision.Candidate!;
                    log?.Invoke($"[switch-back] probing row=\"{candidate.RowName ?? "(unknown)"}\"");
                    SwitchBackProbeResult result;
                    try
                    {
                        result = await probe(candidate, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[switch-back] probe failed ({ex.GetType().Name})");
                        result = SwitchBackProbeResult.Unreachable;
                    }

                    decision = session.NoteProbe(decision.ArmId, result, clock());
                    if (decision.Action != SwitchBackAction.SwitchBack)
                        continue;
                    break;
                }

                case SwitchBackAction.SwitchBack:
                    break;
            }

            var target = decision.Candidate!;
            var why = decision.Reason ?? "recovered";
            log?.Invoke($"[switch-back] returning to row=\"{target.RowName ?? "(unknown)"}\" ({why})");
            try
            {
                await switchBack(target, why, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[switch-back] switch-back failed ({ex.GetType().Name}: {ex.Message})");
                session.Cancel(decision.ArmId, "switch-back failed");
                continue;
            }

            // Only this arm: if the attach failed and re-armed, the new arm stays.
            session.Cancel(decision.ArmId, "switched back");
        }
    }

    private static async Task<bool> DelayAsync(
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        try
        {
            await delay(wait, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
