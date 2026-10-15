namespace VardyParty.Hosting;

/// <summary>
/// Reads the candidate type out of an ICE candidate string so diagnostics can
/// say what ICE gathered (host, srflx, prflx, relay) without recording the
/// address or port that follows.
/// </summary>
internal static class IceCandidateKind
{
    public const string Unknown = "unknown";

    /// <summary>
    /// Returns "host", "srflx", "prflx", "relay", or "unknown" from the token
    /// after "typ". Accepts a leading "candidate:" or "a=candidate:" prefix.
    /// </summary>
    public static string Of(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Unknown;
        }

        var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (!tokens[i].Equals("typ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return tokens[i + 1].ToLowerInvariant() switch
            {
                "host" => "host",
                "srflx" => "srflx",
                "prflx" => "prflx",
                "relay" => "relay",
                _ => Unknown
            };
        }

        return Unknown;
    }
}
