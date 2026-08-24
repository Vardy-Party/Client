using System;
using System.Collections.Generic;

namespace VardyParty.Core.Tests
{
    // Lightweight HTML builder for tests using fluent API
    public class BbcHtmlBuilder
    {
        private string _league = string.Empty;
        private readonly List<string> _games = new();
        private string _initialJson = string.Empty;

        public BbcHtmlBuilder WithLeague(string league)
        {
            _league = league ?? string.Empty;
            return this;
        }

        public BbcHtmlBuilder WithInitialJson(string json)
        {
            _initialJson = json ?? string.Empty;
            return this;
        }

        public BbcHtmlBuilder AddGame(Action<GameBuilder> configure)
        {
            var gb = new GameBuilder();
            configure(gb);
            _games.Add(gb.Build());
            return this;
        }

        public string BuildPage()
        {
            var header = string.IsNullOrEmpty(_league) ? string.Empty : $"<h2>{System.Net.WebUtility.HtmlEncode(_league)}</h2>\n";
            var jsonBlock = string.IsNullOrEmpty(_initialJson) ? string.Empty : $"<script>window.__INITIAL_DATA__ = {_initialJson};</script>\n";
            var body = string.Join("\n", _games);
            return $"<html><body>\n{header}{jsonBlock}{body}\n</body></html>";
        }

        public class GameBuilder
        {
            private string _eventId = "s-1";
            private string _home = "Home";
            private string _away = "Away";
            private int? _homeScore = null;
            private int? _awayScore = null;
            private string _progressText = string.Empty;
            private string _homeBadge = string.Empty;
            private string _awayBadge = string.Empty;

            // After Extra Time and penalty result support
            private bool _afterExtraTime = false;
            private bool _inExtraTime = false;
            private string _penaltyWinner = string.Empty;
            private int? _penaltyWinnerGoals = null;
            private int? _penaltyLoserGoals = null;
            private int? _etMinute = null;
            private int? _homePenalties = null;
            private int? _awayPenalties = null;

            public GameBuilder WithEventId(string id) { _eventId = id ?? _eventId; return this; }
            public GameBuilder WithHome(string home) { _home = home ?? _home; return this; }
            public GameBuilder WithAway(string away) { _away = away ?? _away; return this; }
            public GameBuilder WithScore(int? home, int? away) { _homeScore = home; _awayScore = away; return this; }
            public GameBuilder WithProgressText(string text) { _progressText = text ?? string.Empty; return this; }
            public GameBuilder WithHomeBadge(string url) { _homeBadge = url ?? string.Empty; return this; }
            public GameBuilder WithAwayBadge(string url) { _awayBadge = url ?? string.Empty; return this; }
            public GameBuilder WithAfterExtraTime(bool aet = true) { _afterExtraTime = aet; return this; }
            public GameBuilder WithInExtraTime(int minute) { _inExtraTime = true; _etMinute = minute; return this; }
            public GameBuilder WithPenaltyResult(string winner, int winnerGoals, int loserGoals) { _penaltyWinner = winner ?? string.Empty; _penaltyWinnerGoals = winnerGoals; _penaltyLoserGoals = loserGoals; return this; }
            public GameBuilder WithPenalties(int homePenalties, int awayPenalties) { _homePenalties = homePenalties; _awayPenalties = awayPenalties; return this; }

            public string Build()
            {
                var homeScoreHtml = _homeScore.HasValue ? $"<div class=\"HomeScore\">{_homeScore}</div>" : string.Empty;
                var awayScoreHtml = _awayScore.HasValue ? $"<div class=\"AwayScore\">{_awayScore}</div>" : string.Empty;
                var homeBadgeHtml = string.IsNullOrEmpty(_homeBadge) ? string.Empty : $"<img data-testid=\"badge-img-home\" src=\"{System.Net.WebUtility.HtmlEncode(_homeBadge)}\" />";
                var awayBadgeHtml = string.IsNullOrEmpty(_awayBadge) ? string.Empty : $"<img data-testid=\"badge-img-away\" src=\"{System.Net.WebUtility.HtmlEncode(_awayBadge)}\" />";
                var progressHtml = string.Empty;
                if (!string.IsNullOrEmpty(_progressText) && !_inExtraTime) // Explicit in-game status
                {
                    progressHtml = $"<div class=\"MatchProgressContainer\"><div class=\"StyledPeriod\">{System.Net.WebUtility.HtmlEncode(_progressText)}</div></div>";
                }
                else if (_inExtraTime && _etMinute.HasValue)
                {
                    // ET markup must mimic real BBC structure to satisfy parser
                    // Parser looks for "ET" or "extra time" inside the progress container
                    // The minute + ET is usually something like "100' ET" or just "ET" with minute extracted separately
                    progressHtml = $"<div class=\"MatchProgressContainer\"><div class=\"MatchProgressWrapper\"><span class=\"visually-hidden\">{_etMinute} minutes extra time , in progress</span><div aria-hidden=\"true\" class=\"StyledPeriod\"><div>{_etMinute}' ET</div></div></div></div>";
                }
                else if (_homePenalties.HasValue && _awayPenalties.HasValue)
                {
                    progressHtml = $"<div class=\"MatchProgressContainer\"><div class=\"MatchProgressWrapper\"><span class=\"visually-hidden\">Penalties {System.Net.WebUtility.HtmlEncode(_home)} {_homePenalties} , {System.Net.WebUtility.HtmlEncode(_away)} {_awayPenalties}</span><div aria-hidden=\"true\" class=\"StyledPeriod\"><div>Penalties {_homePenalties}-{_awayPenalties}</div></div></div></div>";
                }

                // AET HTML
                var aetHtml = string.Empty;
                if (_afterExtraTime)
                {
                    aetHtml = "<div class=\"MatchProgressContainer\"><div class=\"MatchProgressWrapper\"><span class=\"visually-hidden\">After extra time</span><div aria-hidden=\"true\" class=\"StyledPeriod\"><div>AET</div></div></div></div>";
                }

                // Penalty HTML
                var penHtml = string.Empty;
                if (!string.IsNullOrEmpty(_penaltyWinner) && _penaltyWinnerGoals.HasValue && _penaltyLoserGoals.HasValue)
                {
                    var winnerEsc = System.Net.WebUtility.HtmlEncode(_penaltyWinner);
                    penHtml = $"<div class=\"PenaltyScoresContainer\"><span class=\"visually-hidden\">{winnerEsc} win {_penaltyWinnerGoals} - {_penaltyLoserGoals} on penalties</span><div aria-hidden=\"true\" class=\"PenaltiesText\"><span class=\"WinningTeamName\">{winnerEsc}</span> win {_penaltyWinnerGoals}-{_penaltyLoserGoals} on pens</div></div>";
                }

                // Optional visually-hidden head-to-head summary (for AET/penalties)
                var hiddenSummary = string.Empty;
                if (_afterExtraTime || (!string.IsNullOrEmpty(_penaltyWinner) && _penaltyWinnerGoals.HasValue && _penaltyLoserGoals.HasValue))
                {
                    var homeEsc = System.Net.WebUtility.HtmlEncode(_home);
                    var awayEsc = System.Net.WebUtility.HtmlEncode(_away);
                    var hs = _homeScore.HasValue ? _homeScore.Value.ToString() : string.Empty;
                    var as_ = _awayScore.HasValue ? _awayScore.Value.ToString() : string.Empty;
                    var parts = new List<string>();
                    parts.Add($"{homeEsc} {hs} , {awayEsc} {as_}");
                    if (_afterExtraTime) parts.Add("After extra time");
                    if (!string.IsNullOrEmpty(_penaltyWinner) && _penaltyWinnerGoals.HasValue && _penaltyLoserGoals.HasValue)
                    {
                        parts.Add($"{System.Net.WebUtility.HtmlEncode(_penaltyWinner)} win {_penaltyWinnerGoals} - {_penaltyLoserGoals} on penalties");
                    }
                    hiddenSummary = $"<div class=\"StyledHeadToHead\"><span class=\"visually-hidden\">{string.Join(", ", parts)}</span></div>\n";
                }

                var block = string.Empty;
                if (!string.IsNullOrEmpty(hiddenSummary)) block += hiddenSummary;

                block += $"<div data-event-id=\"{_eventId}\" class=\"game\">\n" +
                         $"  <div class=\"WithInlineFallback-TeamHome\">\n" +
                         $"    <div class=\"TeamNameWrapper\"><span class=\"DesktopValue\">{System.Net.WebUtility.HtmlEncode(_home)}</span></div>\n" +
                         $"    {homeBadgeHtml}\n" +
                         $"  </div>\n" +
                         $"  <div class=\"WithInlineFallback-TeamAway\">\n" +
                         $"    <div class=\"TeamNameWrapper\"><span class=\"DesktopValue\">{System.Net.WebUtility.HtmlEncode(_away)}</span></div>\n" +
                         $"    {awayBadgeHtml}\n" +
                         $"  </div>\n" +
                         $"  <div class=\"Scores\">{homeScoreHtml}{awayScoreHtml}</div>\n" +
                         $"  {progressHtml}\n" +
                         $"  {aetHtml}\n" +
                         $"</div>\n" +
                         $"{penHtml}";

                return block;
            }
        }
    }
}
