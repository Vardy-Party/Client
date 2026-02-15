namespace VardyParty.Parsers;

public interface IBbcJsonParser
{
    Dictionary<string, (string periodLabel, string status, string statusComment)> BuildEventStatusMapStreaming(string html, CancellationToken cancellationToken = default);
}
