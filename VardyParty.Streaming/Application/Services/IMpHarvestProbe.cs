namespace VardyParty.Streaming;

/// <summary>
/// Lets the resolver see what the running <c>/mp</c> harvest already holds, so the MP
/// lane only starts a row that will answer without waiting on LocalService (rule 7),
/// chips that need nothing more from LocalService can be probed during the
/// post-first-play priority window (rule 11), and a cancelled run lets go of its
/// harvest (docs/PROBE_SCHEDULING_RULES.md). Kept apart from
/// <see cref="ILocalLanPlayService"/> so mocks of that interface are unaffected.
/// </summary>
public interface IMpHarvestProbe
{
    /// <summary>Whether resolving <paramref name="chip"/> on <paramref name="pageUrl"/> would answer from the harvest now, without a <c>POST /mp</c>.</summary>
    bool IsMpChipReady(string pageUrl, string? chip);

    /// <summary>
    /// Whether an MP row for <paramref name="pageUrl"/> / <paramref name="chip"/> may take
    /// the MP lane now. False while a harvest runs and the row would only wait on it:
    /// a chip that harvest has not answered yet, or any row of a different page (which
    /// would queue behind LocalService's single <c>/mp</c>).
    /// </summary>
    bool CanStartMp(string pageUrl, string? chip);

    /// <summary>
    /// A resolver run wants <paramref name="pageUrl"/> until the returned handle is
    /// disposed. When the last caller or run that wanted the running harvest's page
    /// goes away cancelled (<paramref name="runToken"/>), that harvest is cancelled so
    /// LocalService's single <c>/mp</c> is free for the next page.
    /// </summary>
    IDisposable WantMpPage(string pageUrl, CancellationToken runToken);

    /// <summary>Completes when the harvest changes (a chip event, a deadline, its end) or a new harvest starts.</summary>
    Task WaitForMpHarvestChangeAsync(CancellationToken cancellationToken);
}
