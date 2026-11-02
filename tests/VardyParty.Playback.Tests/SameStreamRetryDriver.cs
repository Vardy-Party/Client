using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Playback;
using Xunit;
using Xunit.Sdk;

namespace VardyParty.Playback.Tests;

/// <summary>
/// Acts as the host while the controller retries the same stream. A reload
/// re-attach keeps the generation; a fresh <c>/play</c> becomes a new attach of
/// the same row. The failure repeats until the controller stops retrying, and
/// the effects that end the retries are returned.
/// </summary>
internal static class SameStreamRetryDriver
{
    private const int MaxRounds = 10;

    public static IReadOnlyList<PlaybackEffect> FailUntilSwitch(PlaybackSessionController session, string reason)
        => FailUntilSwitch(session, () => session.Handle(MediaEngineEvent.Error(session.Snapshot.AttachGeneration, reason)));

    public static IReadOnlyList<PlaybackEffect> FailUntilSwitch(
        PlaybackSessionController session,
        Func<IReadOnlyList<PlaybackEffect>> raiseFailure)
    {
        var effects = raiseFailure();
        for (var round = 0; round < MaxRounds; round++)
        {
            if (!IsRetry(effects))
                return effects;

            Assert.DoesNotContain(effects, e => e.Kind is PlaybackEffectKind.ReportFailed or PlaybackEffectKind.ReportDeclined);
            var fresh = effects.FirstOrDefault(e => e.Kind == PlaybackEffectKind.RetryFreshResolve);
            if (fresh is not null)
                session.BeginAttach(fresh.Url ?? session.Snapshot.CurrentUrl!, usedCachedUrl: false, force: true);

            effects = raiseFailure();
        }

        throw new XunitException($"Controller still retrying after {MaxRounds} failures");
    }

    /// <summary>
    /// A health decline gets one reload. The stream plays again (Ready), then
    /// declines a second time, which switches.
    /// </summary>
    public static IReadOnlyList<PlaybackEffect> DeclineAfterReload(
        PlaybackSessionController session,
        Func<IReadOnlyList<PlaybackEffect>> declineOnce)
    {
        var first = declineOnce();
        Assert.Contains(first, e => e.Kind == PlaybackEffectKind.Attach && e.Url == session.Snapshot.CurrentUrl);
        Assert.DoesNotContain(first, e => e.Kind is PlaybackEffectKind.ReportDeclined or PlaybackEffectKind.AdvanceToNext);
        session.Handle(MediaEngineEvent.Ready(session.Snapshot.AttachGeneration));
        return declineOnce();
    }

    public static bool IsRetry(IReadOnlyList<PlaybackEffect> effects) =>
        effects.Any(e => e.Kind is PlaybackEffectKind.RetryFreshResolve or PlaybackEffectKind.Attach)
        && !effects.Any(e => e.Kind is PlaybackEffectKind.AdvanceToNext or PlaybackEffectKind.CloseSession
            or PlaybackEffectKind.RemoveCurrentFromPool or PlaybackEffectKind.RevertToLastGood);
}