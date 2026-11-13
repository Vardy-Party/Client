namespace VardyParty.Streaming;

/// <summary>
/// Lets the resolver see what the running <c>/mp</c> harvest already holds, so chips
/// that need nothing more from LocalService can be probed during the post-first-play
/// priority window (docs/PROBE_SCHEDULING_RULES.md rule 11). Kept apart from
/// <see cref="ILocalLanPlayService"/> so mocks of that interface are unaffected.
/// </summary>
public interface IMpHarvestProbe
{
    /// <summary>Whether resolving <paramref name="chip"/> on <paramref name="pageUrl"/> would answer from the harvest now, without a <c>POST /mp</c>.</summary>
    bool IsMpChipReady(string pageUrl, string? chip);

    /// <summary>Completes when the harvest changes (a chip event, its end) or a new harvest starts.</summary>
    Task WaitForMpHarvestChangeAsync(CancellationToken cancellationToken);
}
