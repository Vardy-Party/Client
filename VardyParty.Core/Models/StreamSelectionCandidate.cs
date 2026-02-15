namespace VardyParty.Models;

using StreamModel = VardyParty.Models.Stream;

public class StreamSelectionCandidate
{
    public int Index { get; set; }
    public required StreamModel Stream { get; set; }
    public string NormalizedUrl { get; set; } = string.Empty;
}
