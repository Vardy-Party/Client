namespace VardyParty.Models;

public record BbcFixture(
    string Home,
    string Away,
    DateTime KickoffUtc,
    string Status,
    bool IsFinished,
    bool IsInProgress,
    bool IsHalfTime,
    int? Minute,
    int? HomeScore,
    int? AwayScore,
    string HomeBadgeUrl,
    string AwayBadgeUrl,
    string League,
    bool HasProgress,
    bool AfterExtraTime = false,
    string PenaltyWinner = "",
    int? PenaltyWinnerGoals = null,
    int? PenaltyLoserGoals = null);
