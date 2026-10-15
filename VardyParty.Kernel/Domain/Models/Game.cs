namespace VardyParty.Kernel;

public class Game
{
    public string Href { get; set; } = string.Empty;
    public string Home { get; set; } = string.Empty;
    public string Away { get; set; } = string.Empty;

    /// <summary>
    /// Fixture identity shared by the catalog grace clock and the homepage
    /// board. Trimmed raw home and away names, uppercased so a display-name
    /// change from BBC enrichment does not split one match into two keys.
    /// </summary>
    public string FixtureKey =>
        $"{(Home ?? string.Empty).Trim()}|{(Away ?? string.Empty).Trim()}".ToUpperInvariant();
    public DateTime Start { get; set; }

    // BBC enrichment
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public int? AggregateHomeScore { get; set; }
    public int? AggregateAwayScore { get; set; }
    public bool IsFinished { get; set; }
    public bool IsInProgress { get; set; }
    public bool IsHalfTime { get; set; }
    public int? Minute { get; set; }
    public string StatusText { get; set; } = string.Empty;

    // Logos (BBC badge svg)
    public string HomeBadgeUrl { get; set; } = string.Empty;
    public string AwayBadgeUrl { get; set; } = string.Empty;

    public string League { get; set; } = string.Empty;
    public string ApiLeague { get; set; } = string.Empty;

    // BBC source fields
    public string BBCHome { get; set; } = string.Empty;
    public string BBCAway { get; set; } = string.Empty;

    /// <summary>
    /// BBC's own (canonical) name for the competition, exactly as BBC supplies it, for BBC-matched games.
    /// Never holds the API league name: that lives in <see cref="ApiLeague"/>.
    /// </summary>
    public string BBCLeague { get; set; } = string.Empty;

    /// <summary>
    /// The league name this game is shown and grouped under once BBC enrichment has been reconciled with the
    /// API league (the API's name when BBC spells the same competition differently, so matched and unmatched
    /// games share one section; a canonical name for international fixtures; otherwise BBC's name). Set only
    /// by the matcher through <see cref="ResolveLeague"/>, which is why the setter is private: it is not part
    /// of the API payload and is not populated by deserialisation.
    /// </summary>
    public string ResolvedLeague { get; private set; } = string.Empty;

    /// <summary>Records the reconciled display league (see <see cref="ResolvedLeague"/>).</summary>
    public void ResolveLeague(string? resolvedLeague) => ResolvedLeague = (resolvedLeague ?? string.Empty).Trim();

    // Display helpers prefer the reconciled league, then the BBC-provided values, when available
    public string DisplayHome => string.IsNullOrWhiteSpace(BBCHome) ? (Home ?? string.Empty).Trim() : BBCHome.Trim();
    public string DisplayAway => string.IsNullOrWhiteSpace(BBCAway) ? (Away ?? string.Empty).Trim() : BBCAway.Trim();
    public string DisplayLeague =>
        !string.IsNullOrWhiteSpace(ResolvedLeague) ? ResolvedLeague.Trim()
        : !string.IsNullOrWhiteSpace(BBCLeague) ? BBCLeague.Trim()
        : (League ?? string.Empty).Trim();
    public bool IsOlympicLeague => !string.IsNullOrWhiteSpace(DisplayLeague)
        && DisplayLeague.Contains("Olympic", StringComparison.OrdinalIgnoreCase);

    private bool StatusTextIndicatesLive
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StatusText)) return false;
            return StatusText.Contains("live", StringComparison.OrdinalIgnoreCase)
                || StatusText.Contains("in progress", StringComparison.OrdinalIgnoreCase)
                || StatusText.Contains("HT", StringComparison.OrdinalIgnoreCase)
                || StatusText.Contains("'", StringComparison.OrdinalIgnoreCase);
        }
    }

    private int? MinuteFromStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StatusText)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(StatusText, @"(?<base>\d+)(?:\+(?<extra>\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            if (!int.TryParse(match.Groups["base"].Value, out var baseMin)) return null;
            if (match.Groups["extra"].Success && int.TryParse(match.Groups["extra"].Value, out var extra))
            {
                return baseMin * 100 + extra; // encode stoppage time
            }
            return baseMin;
        }
    }

    // Check for "Postponed" in status text or "P" abbreviation
    public bool IsPostponed => (!string.IsNullOrEmpty(StatusText) && StatusText.Contains("postponed", StringComparison.OrdinalIgnoreCase)) || (!string.IsNullOrEmpty(StatusText) && StatusText.Equals("P", StringComparison.OrdinalIgnoreCase));

    // Helpers for ordering
    public bool IsPostponedForOrdering => IsPostponed;

    public bool IsLiveForOrdering => IsInProgress || IsHalfTime || (!IsFinished && (Minute.HasValue || StatusTextIndicatesLive));

    public DateTime StartUtcForOrdering
    {
        get
        {
            if (Start == default) return DateTime.MaxValue;
            return Start.Kind == DateTimeKind.Utc ? Start : Start.ToUniversalTime();
        }
    }

    public int SortTierForOrdering
    {
        get
        {
            if (IsLiveForOrdering) return 0;
            if (!IsFinished && !IsPostponedForOrdering) return 1; // scheduled/upcoming
            if (IsPostponedForOrdering && !IsFinished) return 2;
            return 3; // finished or unknown
        }
    }

    public double LiveMinuteForOrdering
    {
        get
        {
            if (!IsLiveForOrdering) return -1;
            if (IsHalfTime) return 45.99; // above any 45+ stoppage
            var minuteVal = Minute ?? MinuteFromStatus;
            if (minuteVal.HasValue)
            {
                var m = minuteVal.Value;
                if (m >= 1000)
                {
                    var baseMin = m / 100;
                    var extra = m % 100;
                    return baseMin + extra * 0.01; // e.g., 45+2 => 45.02
                }
                return m;
            }
            return -1; // unknown live minute treated lowest among live so known minutes sort first
        }
    }

    public bool IsScheduledUpcoming(DateTime nowUtc)
    {
        if (IsPostponed) return false;

        var startUtc = StartUtcForOrdering;
        if (startUtc == default || startUtc == DateTime.MaxValue) return false;

        // Trust a future kickoff over stale in-play/finished flags from the API.
        if (startUtc > nowUtc.AddMinutes(5)) return true;

        if (startUtc <= nowUtc.AddMinutes(1)) return false;
        if (IsInProgress || IsHalfTime || Minute is > 0) return false;

        return !IsFinished && !IsLiveForOrdering;
    }

    public string DisplayStatusText()
    {
        if (IsFinished) return "FT";
        if (IsHalfTime) return "HT";
        if (IsInProgress && !string.IsNullOrEmpty(StatusText)) return StatusText;
        var minuteVal = Minute ?? MinuteFromStatus;
        if (IsInProgress && minuteVal.HasValue)
        {
            var m = minuteVal.Value;
            if (m >= 1000)
            {
                var baseMin = m / 100;
                var extra = m % 100;
                return $"{baseMin}+{extra}'";
            }
            return $"{m}'";
        }
        if (!string.IsNullOrEmpty(StatusText)) return StatusText;
        return string.Empty;
    }
}
