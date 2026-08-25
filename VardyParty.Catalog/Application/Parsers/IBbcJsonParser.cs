namespace VardyParty.Catalog;

public interface IBbcJsonParser
{
    Dictionary<string, (string periodLabel, string status, string statusComment)> BuildEventStatusMapStreaming(
        string html,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Single pass over embedded __INITIAL_DATA__ for event status and kickoff times.
    /// </summary>
    (Dictionary<string, (string periodLabel, string status, string statusComment)> StatusByEventId,
        Dictionary<string, DateTime> KickoffUtcByEventId) BuildEventMapsStreaming(
        string html,
        CancellationToken cancellationToken = default);
}
