namespace VardyParty.Models;

public class Game
{
    public string Href { get; set; } = string.Empty;
    public string Home { get; set; } = string.Empty;
    public string Away { get; set; } = string.Empty;
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
    public string BBCLeague { get; set; } = string.Empty;

    // Display helpers prefer BBC-provided value when available
    public string DisplayHome => string.IsNullOrWhiteSpace(BBCHome) ? (Home ?? string.Empty).Trim() : BBCHome.Trim();
    public string DisplayAway => string.IsNullOrWhiteSpace(BBCAway) ? (Away ?? string.Empty).Trim() : BBCAway.Trim();
    public string DisplayLeague => string.IsNullOrWhiteSpace(BBCLeague) ? (League ?? string.Empty).Trim() : BBCLeague.Trim();
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
