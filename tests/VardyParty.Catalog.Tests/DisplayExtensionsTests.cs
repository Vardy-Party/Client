using System;
using System.Collections.Generic;
using System.Linq;
using AutoFixture;
using VardyParty.Catalog;
using VardyParty.Kernel;
using Xunit;
using VardyParty.TestSupport;

namespace VardyParty.Catalog.Tests
{
    public class DisplayExtensionsTests
    {
        private readonly IFixture _fixture = AutoMoqFixture.Create();

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
            return _fixture.Build<Game>()
                .With(g => g.Home, home)
                .With(g => g.Away, away)
                .With(g => g.Start, startUtc)
                .With(g => g.IsFinished, isFinished)
                .With(g => g.IsInProgress, isInProgress)
                .With(g => g.Minute, minute)
                .With(g => g.HomeScore, homeScore)
                .With(g => g.AwayScore, awayScore)
                .With(g => g.StatusText, statusText)
                .With(g => g.IsHalfTime, isHalfTime)
                .With(g => g.League, league)
                .With(g => g.BBCHome, string.Empty)
                .With(g => g.BBCAway, string.Empty)
                .With(g => g.BBCLeague, string.Empty)
                .Create();
        }

        [Fact]
        public void EmptyInput_ReturnsEmptyList()
        {
            // Arrange
            Dictionary<string, List<Game>>? nullDict = null;
            var emptyDict = new Dictionary<string, List<Game>>();

            // Act
            var nullResult = nullDict.ToDisplay();
            var emptyResult = emptyDict.ToDisplay();

            // Assert
            Assert.Empty(nullResult);
            Assert.Empty(emptyResult);
        }

        [Fact]
        public void LiveMinute_FromStatusText_AffectsOrdering()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var g60 = Make("Home United", "Away City", now.AddMinutes(-30), isInProgress: true, minute: 60, league: "League Alpha");
            var g67text = Make("North FC", "South FC", now.AddMinutes(-20), isInProgress: false, minute: null, statusText: "67'", league: "League Alpha");
            var dict = new Dictionary<string, List<Game>> { ["League Alpha"] = new List<Game> { g60, g67text } };

            // Act
            var ordered = dict.ToDisplay();

            // Assert
            // g67text should come before g60 because its parsed minute is higher
            Assert.Equal(g67text, ordered.First());
            Assert.Equal(g60, ordered.Skip(1).First());
        }

        [Fact]
        public void AetAndPenalties_AreVisibleWhenLive()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var aet = Make("Home United", "Away City", now.AddMinutes(-120), isFinished: false, isInProgress: true, minute: 105, statusText: "After extra time", league: "League Delta");
            var pens = Make("North FC", "South FC", now.AddMinutes(-125), isFinished: false, isInProgress: true, minute: 120, statusText: "Penalties 4-3", league: "League Delta");
            var dict = new Dictionary<string, List<Game>> { ["League Delta"] = new List<Game> { aet, pens } };

            // Act
            var ordered = dict.ToDisplay();

            // Assert
            Assert.Contains(aet, ordered);
            Assert.Contains(pens, ordered);
        }

        [Fact]
        public void Postponed_AreOrderedAfter_Upcoming()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var upcoming = Make("Home United", "Away City", now.AddHours(1), isFinished: false, statusText: "", league: "League Alpha");
            var postponed = Make("North FC", "South FC", now.AddHours(1), isFinished: false, statusText: "Match postponed due to weather", league: "League Alpha");
            var dict = new Dictionary<string, List<Game>> { ["League Alpha"] = new List<Game> { postponed, upcoming } };

            // Act
            var ordered = dict.ToDisplay();

            // Assert
            Assert.Equal(upcoming, ordered.First());
            Assert.Equal(postponed, ordered.Last());
        }

        [Fact]
        public void TieBreak_By_DisplayHome_Alphabetical()
        {
            // Arrange
            var now = DateTime.UtcNow;
            // Same tier/start/minute -> tie-break on DisplayHome
            var gA = Make("Alpha United", "Away City", now.AddMinutes(-10), isInProgress: true, minute: 25, league: "League Beta");
            var gB = Make("Zoo FC", "South FC", now.AddMinutes(-10), isInProgress: true, minute: 25, league: "League Beta");
            var dict = new Dictionary<string, List<Game>> { ["League Beta"] = new List<Game> { gB, gA } };

            // Act
            var ordered = dict.ToDisplay();

            // Assert
            Assert.Equal(gA, ordered.First());
            Assert.Equal(gB, ordered.Last());
        }

        [Fact]
        public void ToDisplay_IncludesLateNightKickoffWithinLookAheadWindow()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var kickoff = BbcFixtureSchedule.GetLookAheadEndUtc(now).AddHours(-1);
            var game = Make("Home United", "Away City", kickoff, league: "League Alpha");
            var dict = new Dictionary<string, List<Game>> { ["League Alpha"] = [game] };

            // Act
            var ordered = dict.ToDisplay();

            // Assert
            Assert.True(BbcFixtureSchedule.IsWithinLookAheadWindow(kickoff, now));
            Assert.Contains(game, ordered);
        }

        [Fact]
        public void ToDisplay_ExcludesStalePastKickoff()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var stale = Make("North FC", "South FC", now.AddHours(-6), league: "League Gamma");
            var upcoming = Make("Home United", "Away City", now.AddHours(2), league: "League Alpha");
            var dict = new Dictionary<string, List<Game>>
            {
                ["League Gamma"] = [stale],
                ["League Alpha"] = [upcoming]
            };

            // Act
            var ordered = dict.ToDisplay();

            // Assert
            Assert.DoesNotContain(stale, ordered);
            Assert.Contains(upcoming, ordered);
        }

        [Fact]
        public void Consumer_CanFilter_IgnoredLeagues()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var game1 = Make("North FC", "South FC", now, league: "League Hidden");
            var game2 = Make("Home United", "Away City", now, league: "League Alpha");
            var dict = new Dictionary<string, List<Game>> { ["League Hidden"] = new List<Game> { game1 }, ["League Alpha"] = new List<Game> { game2 } };
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "League Hidden" };

            // Act
            var ordered = dict.ToDisplay();
            var consumerVisible = ordered.Where(g => !ignored.Contains(g.League)).ToList();

            // Assert
            Assert.DoesNotContain(consumerVisible, g => g.League.Equals("League Hidden", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(consumerVisible, g => g.League.Equals("League Alpha", StringComparison.OrdinalIgnoreCase));
        }
    }
}
