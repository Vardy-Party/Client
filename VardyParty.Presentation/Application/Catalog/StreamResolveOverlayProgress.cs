namespace VardyParty.Presentation;

/// <summary>
/// Finding-streams overlay: how full the bar is, and when it must spin
/// instead of sitting at 0. Android TV's determinate <c>ProgressBar</c>
/// draws nothing at progress 0, so the first seconds of a pick look idle.
/// </summary>
public static class StreamResolveOverlayProgress
{
    public static double Fraction(int streamsTested, int totalStreams)
    {
        if (totalStreams <= 0)
            return 0;

        return Math.Clamp((double)streamsTested / totalStreams, 0, 1);
    }

    /// <summary>
    /// Spin until the resolver publishes a candidate total. Stop spinning on
    /// the no-healthy dead end so the empty bar is not mistaken for work.
    /// </summary>
    public static bool IsIndeterminate(int totalStreams, bool noHealthyFound) =>
        totalStreams <= 0 && !noHealthyFound;

    /// <summary>
    /// Orchestrator copy is "No working streams found" / "No streams found"
    /// (not "No healthy streams"). Hosts used the latter and never left spin.
    /// </summary>
    public static bool IsExhaustedStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return false;

        return status.Contains("No working streams", StringComparison.OrdinalIgnoreCase)
            || status.Contains("No streams found", StringComparison.OrdinalIgnoreCase)
            || status.Contains("No healthy streams", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hide status text that only repeats the modal title ("Finding streams" /
    /// "Searching for streams", with or without trailing ellipsis). Matchup
    /// subtitles and progress statuses stay visible.
    /// </summary>
    public static bool ShouldShowStatusSubtitle(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var normalized = status.Trim().TrimEnd('.').Trim();
        return !string.Equals(normalized, "Searching for streams", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, "Finding streams", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Count line for the finding-streams modal. While the bar is indeterminate
    /// (candidate total unknown), "0 tested • 0 healthy" is meaningless — return
    /// empty so hosts hide the label.
    /// </summary>
    public static string FormatCountLabel(
        int streamsTested,
        int healthyStreams,
        int totalStreams,
        bool indeterminate)
    {
        if (indeterminate)
            return string.Empty;

        if (totalStreams > 0)
            return $"{totalStreams} total • {streamsTested} tested • {healthyStreams} healthy";

        return $"{streamsTested} tested • {healthyStreams} healthy";
    }
}
