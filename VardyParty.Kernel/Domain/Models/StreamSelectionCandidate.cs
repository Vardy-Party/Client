using StreamModel = VardyParty.Kernel.Stream;

namespace VardyParty.Kernel;

public class StreamSelectionCandidate
{
    public int Index { get; set; }
    public required StreamModel Stream { get; set; }
    public string NormalizedUrl { get; set; } = string.Empty;
}
