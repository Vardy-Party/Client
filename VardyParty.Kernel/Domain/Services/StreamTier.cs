namespace VardyParty.Kernel;

/// <summary>
/// A row's recommended tier (docs/PROBE_SCHEDULING_RULES.md rule 1). Lower is better: the
/// backup is the best-tier playable row other than the one playing (rule 11).
/// </summary>
public enum StreamTier
{
    /// <summary>A High-confidence recommendation (or a chip of a High-recommended MP page).</summary>
    High = 0,

    /// <summary>A Medium-confidence recommendation (or a chip of a Medium-recommended MP page).</summary>
    Medium = 1,

    /// <summary>Everything else: Low recommendations, MP pages and FB rows.</summary>
    Finding = 2
}
