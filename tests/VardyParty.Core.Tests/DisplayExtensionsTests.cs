using System;
using System.Collections.Generic;
using System.Linq;
using VardyParty.Extensions;
using VardyParty.Models;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class DisplayExtensionsTests
    {
        private Game Make(
            string home,
            string away,
            DateTime startUtc,
            bool isFinished = false,
            bool isInProgress = false,
            int? minute = null,
            int? homeScore = null,
            int? awayScore = null,
            string statusText = "",
            bool isHalfTime = false,
            string league = "")
        {
            return new Game
            {
                Home = home,
                Away = away,
                Start = startUtc,
                IsFinished = isFinished,
                IsInProgress = isInProgress,
                Minute = minute,
                HomeScore = homeScore,
                AwayScore = awayScore,
                StatusText = statusText,
                IsHalfTime = isHalfTime,
                League = league
            };
        }

        [Fact]
        public void EmptyInput_ReturnsEmptyList()
        {
            List<Game>? result = null;
            Dictionary<string, List<Game>>? dict = null;
            result = dict.ToDisplay();
            Assert.Empty(result);

            dict = new Dictionary<string, List<Game>>();
            result = dict.ToDisplay();
            Assert.Empty(result);
        }

        [Fact]
        public void LiveMinute_FromStatusText_AffectsOrdering()
        {
            var now = DateTime.UtcNow;
            var g60 = Make("Old","Team", now.AddMinutes(-30), isInProgress: true, minute: 60, league: "L");
            var g67text = Make("New","Team", now.AddMinutes(-20), isInProgress: false, minute: null, statusText: "67'", league: "L");

            var dict = new Dictionary<string, List<Game>> { ["L"] = new List<Game> { g60, g67text } };
            var ordered = dict.ToDisplay();

            // g67text should come before g60 because its parsed minute is higher
            Assert.Equal(g67text, ordered.First());
            Assert.Equal(g60, ordered.Skip(1).First());
        }

        [Fact]
        public void AetAndPenalties_AreVisibleWhenLive()
        {
            var now = DateTime.UtcNow;
            var aet = Make("AET","Team", now.AddMinutes(-120), isFinished: false, isInProgress: true, minute: 105, statusText: "After extra time", league: "Cup");
            var pens = Make("Pens","Team", now.AddMinutes(-125), isFinished: false, isInProgress: true, minute: 120, statusText: "Penalties 4-3", league: "Cup");

            var dict = new Dictionary<string, List<Game>> { ["Cup"] = new List<Game> { aet, pens } };
            var ordered = dict.ToDisplay();

            Assert.Contains(aet, ordered);
            Assert.Contains(pens, ordered);
        }

        [Fact]
        public void Postponed_AreOrderedAfter_Upcoming()
        {
            var now = DateTime.UtcNow;
            var upcoming = Make("Up","Team", now.AddHours(1), isFinished: false, statusText: "", league: "L1");
            var postponed = Make("Post","Team", now.AddHours(1), isFinished: false, statusText: "Match postponed due to weather", league: "L1");

            var dict = new Dictionary<string, List<Game>> { ["L1"] = new List<Game> { postponed, upcoming } };
            var ordered = dict.ToDisplay();

            Assert.Equal(upcoming, ordered.First());
            Assert.Equal(postponed, ordered.Last());
        }

        [Fact]
        public void TieBreak_By_DisplayHome_Alphabetical()
        {
            var now = DateTime.UtcNow;
            // Same tier/start/minute -> tie-break on DisplayHome
            var gA = Make("Alpha","X", now.AddMinutes(-10), isInProgress: true, minute: 25, league: "L2");
            var gB = Make("Zoo","Y", now.AddMinutes(-10), isInProgress: true, minute: 25, league: "L2");

            var dict = new Dictionary<string, List<Game>> { ["L2"] = new List<Game> { gB, gA } };
            var ordered = dict.ToDisplay();

            Assert.Equal(gA, ordered.First());
            Assert.Equal(gB, ordered.Last());
        }

        [Fact]
        public void ToDisplay_IncludesLateNightKickoffWithinLookAheadWindow()
        {
            var now = DateTime.UtcNow;
            var kickoff = BbcFixtureSchedule.GetLookAheadEndUtc(now).AddHours(-1);
            var game = Make("USA", "Paraguay", kickoff, league: "FIFA World Cup");

            var dict = new Dictionary<string, List<Game>> { ["FIFA World Cup"] = [game] };

            Assert.True(BbcFixtureSchedule.IsWithinLookAheadWindow(kickoff, now));
            Assert.Contains(game, dict.ToDisplay());
        }

        [Fact]
        public void ToDisplay_ExcludesStalePastKickoff()
        {
            var now = DateTime.UtcNow;
            var stale = Make("Saudi Arabia", "Laos", now.AddHours(-6), league: "International");
            var upcoming = Make("Malaga", "Almería", now.AddHours(2), league: "La Liga");

            var dict = new Dictionary<string, List<Game>>
            {
                ["International"] = [stale],
                ["La Liga"] = [upcoming]
            };

            var ordered = dict.ToDisplay();

            Assert.DoesNotContain(stale, ordered);
            Assert.Contains(upcoming, ordered);
        }

        [Fact]
        public void Consumer_CanFilter_IgnoredLeagues()
        {
            var now = DateTime.UtcNow;
            var game1 = Make("A","B", now, league: "WWE");
            var game2 = Make("C","D", now, league: "Premier League");

            var dict = new Dictionary<string, List<Game>> { ["WWE"] = new List<Game> { game1 }, ["Premier League"] = new List<Game> { game2 } };
            var ordered = dict.ToDisplay();

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WWE" };
            var consumerVisible = ordered.Where(g => !ignored.Contains(g.League)).ToList();

            Assert.DoesNotContain(consumerVisible, g => g.League.Equals("WWE", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(consumerVisible, g => g.League.Equals("Premier League", StringComparison.OrdinalIgnoreCase));
        }
    }
}
