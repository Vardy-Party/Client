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
    public class GameMatcherTests
    {
        private readonly IFixture _fixture = AutoMoqFixture.Create();

        [Fact]
        public void ExactMatch_EnrichesProperties_InProgress()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "Live",
                    IsFinished = false,
                    IsInProgress = true,
                    IsHalfTime = false,
                    Minute = 22,
                    HomeScore = 0,
                    AwayScore = 10,
                    HomeBadgeUrl = "homebadge",
                    AwayBadgeUrl = "awaybadge",
                    League = "League Alpha",
                    HasProgress = true
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal(0, g.HomeScore);
            Assert.Equal(10, g.AwayScore);
            Assert.Equal(22, g.Minute);
            Assert.True(g.IsInProgress);
            Assert.False(g.IsFinished);
            Assert.Equal("homebadge", g.HomeBadgeUrl);
            Assert.Equal("League Alpha", g.BBCLeague);
        }

        [Fact]
        public void ExactMatch_EnrichesProperties_Finished()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "Live",
                    IsFinished = true,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = 0,
                    AwayScore = 10,
                    HomeBadgeUrl = "homebadge",
                    AwayBadgeUrl = "awaybadge",
                    League = "League Alpha",
                    HasProgress = true
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal(0, g.HomeScore);
            Assert.Equal(10, g.AwayScore);
            Assert.Null(g.Minute);
            Assert.False(g.IsInProgress);
            Assert.True(g.IsFinished);
            Assert.Equal("homebadge", g.HomeBadgeUrl);
            Assert.Equal("League Alpha", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_Acronym_Matches()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "HU")
                    .With(g => g.Away, "Away City")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Home United", g.BBCHome);
            Assert.Equal("League Alpha", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_ShortPrefix_MatchesExpandedName()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Riv Home")
                    .With(g => g.Away, "Bright")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "River Home",
                    Away = "Bright Shore",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("River Home", g.BBCHome);
            Assert.Equal("Bright Shore", g.BBCAway);
            Assert.Equal("League Alpha", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_ShortAlPrefix_DoesNotTreatAlAsStopword()
        {
            // Verifies "Al" is not treated as a stopword, so short names like "Rivem" vs "Riveme"
            // do not take a disproportionate Levenshtein penalty.

            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Al Rivem")
                    .With(g => g.Away, " Al Southa")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Al Riveme",
                    Away = "Al Southa",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Beta",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Beta");

            // Assert
            var g = games.First();
            Assert.Equal("Al Riveme", g.BBCHome);
            Assert.Equal("Al Southa", g.BBCAway);
            Assert.Equal("League Beta", g.BBCLeague);
        }

        [Fact]
        public void EnrichGame_PostponedStatus_MapsToGameStatus()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "Postponed",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "homebadge",
                    AwayBadgeUrl = "awaybadge",
                    League = "League Alpha",
                    HasProgress = false,
                    AfterExtraTime = false,
                    PenaltyWinner = string.Empty,
                    PenaltyWinnerGoals = null,
                    PenaltyLoserGoals = null
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Home United", g.BBCHome);
            Assert.Equal("Postponed", g.StatusText);
            Assert.True(g.IsPostponed, "Game.IsPostponed should be true");
        }

        [Fact]
        public void FuzzyMatch_Diacritics_MatchesAsciiForm()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Iskandir")
                    .With(g => g.Away, "Karagul")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "İskandir",
                    Away = "Karagül",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Beta",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Beta");

            // Assert
            var g = games.First();
            Assert.Equal("İskandir", g.BBCHome);
            Assert.Equal("Karagül", g.BBCAway);
            Assert.Equal("League Beta", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_SingleLetterTypo_Matches()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Rivertn")
                    .With(g => g.Away, "Away City")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Riverton",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Riverton", g.BBCHome);
            Assert.Equal("Away City", g.BBCAway);
            Assert.Equal("League Alpha", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_ExtraQualifier_MatchesShorterAway()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Metro Away City")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Home United", g.BBCHome);
            Assert.Equal("Away City", g.BBCAway);
            Assert.Equal("League Alpha", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_SpecialCharacter_MatchesAsciiForm()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Besika")
                    .With(g => g.Away, "Southria")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Beşika",
                    Away = "Southria",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Beta",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Beta");

            // Assert
            var g = games.First();
            Assert.Equal("Beşika", g.BBCHome);
            Assert.Equal("Southria", g.BBCAway);
            Assert.Equal("League Beta", g.BBCLeague);
        }

        [Fact]
        public void ExactMatch_EnrichesWithBadgeUrls()
        {
            // Arrange
            var apiStartTime = new DateTime(2026, 1, 19, 19, 45, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.Start, apiStartTime)
                    .With(g => g.League, "League Alpha")
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbcKickoff = new DateTime(2026, 1, 19, 19, 45, 0, DateTimeKind.Utc);
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = bbcKickoff,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "https://badges.example/home-united.svg",
                    AwayBadgeUrl = "https://badges.example/away-city.webp",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Home United", g.BBCHome);
            Assert.Equal("Away City", g.BBCAway);
            Assert.Equal("League Alpha", g.BBCLeague);
            Assert.Equal("https://badges.example/home-united.svg", g.HomeBadgeUrl);
            Assert.Equal("https://badges.example/away-city.webp", g.AwayBadgeUrl);
        }

        [Fact]
        public void FuzzyMatch_Abbreviation_MatchesExpandedName()
        {
            // Arrange
            var apiStartTime = new DateTime(2026, 1, 20, 20, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home Utd")
                    .With(g => g.Away, "Away City")
                    .With(g => g.Start, apiStartTime)
                    .With(g => g.League, "Cup Gamma")
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = apiStartTime,
                    Status = "20:00",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "https://badges.example/home-united.svg",
                    AwayBadgeUrl = "https://badges.example/away-city.svg",
                    League = "Cup Gamma",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "Cup Gamma");

            // Assert
            var g = games.First();
            Assert.Equal("Home United", g.BBCHome);
            Assert.Equal("Away City", g.BBCAway);
            Assert.Equal("Cup Gamma", g.BBCLeague);
            Assert.Equal("https://badges.example/home-united.svg", g.HomeBadgeUrl);
            Assert.Equal("https://badges.example/away-city.svg", g.AwayBadgeUrl);
        }

        [Fact]
        public void FuzzyMatch_LongForm_MatchesContainedShortName()
        {
            // Arrange
            var apiStartTime = new DateTime(2026, 1, 20, 20, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home International")
                    .With(g => g.Away, "Away City")
                    .With(g => g.Start, apiStartTime)
                    .With(g => g.League, "Cup Gamma")
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home",
                    Away = "Away City",
                    KickoffUtc = apiStartTime,
                    Status = "20:00",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "https://badges.example/home.svg",
                    AwayBadgeUrl = "https://badges.example/away-city.svg",
                    League = "Cup Gamma",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "Cup Gamma");

            // Assert
            var g = games.First();
            Assert.Equal("Home", g.BBCHome);
            Assert.Equal("Away City", g.BBCAway);
            Assert.Equal("Cup Gamma", g.BBCLeague);
            Assert.Equal("https://badges.example/home.svg", g.HomeBadgeUrl);
            Assert.Equal("https://badges.example/away-city.svg", g.AwayBadgeUrl);
        }

        [Fact]
        public void ExactMatch_EnrichesAggregateScores()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.League, string.Empty)
                    .With(g => g.ApiLeague, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "FT",
                    IsFinished = true,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = 1,
                    AwayScore = 0,
                    HomeBadgeUrl = "",
                    AwayBadgeUrl = "",
                    League = "League Alpha",
                    HasProgress = true,
                    AfterExtraTime = false,
                    PenaltyWinner = "",
                    PenaltyWinnerGoals = null,
                    PenaltyLoserGoals = null,
                    AggregateHomeScore = 5,
                    AggregateAwayScore = 3
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal(5, g.AggregateHomeScore);
            Assert.Equal(3, g.AggregateAwayScore);
            Assert.Equal(1, g.HomeScore);
            Assert.Equal(0, g.AwayScore);
        }

        [Fact]
        public void FuzzyMatch_Acronym_AdoptsBbcKickoff()
        {
            // Arrange
            var apiWrongStart = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);
            var bbcKickoff = new DateTime(2026, 6, 13, 1, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "HU")
                    .With(g => g.Away, "Away City")
                    .With(g => g.Start, apiWrongStart)
                    .With(g => g.ApiLeague, "League Alpha")
                    .With(g => g.League, "League Alpha")
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = bbcKickoff,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "home-united.svg",
                    AwayBadgeUrl = "away-city.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Home United", g.BBCHome);
            Assert.Equal("League Alpha", g.BBCLeague);
            Assert.Equal(bbcKickoff, g.Start);
        }

        [Fact]
        public void FutureKickoff_WithStaleMinuteZero_IsScheduledUpcoming()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var futureKickoff = now.AddHours(2);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.Start, futureKickoff)
                    .With(g => g.Minute, 0)
                    .With(g => g.IsInProgress, true)
                    .With(g => g.IsFinished, true)
                    .With(g => g.League, "Cup Gamma")
                    .With(g => g.ApiLeague, string.Empty)
                    .With(g => g.StatusText, string.Empty)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = futureKickoff,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "home-united.svg",
                    AwayBadgeUrl = "away-city.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.False(g.IsInProgress);
            Assert.False(g.IsFinished);
            Assert.Null(g.Minute);
            Assert.True(g.IsScheduledUpcoming(now));
        }

        [Fact]
        public void IsScheduledUpcoming_FutureKickoff_IgnoresStaleInProgressFlag()
        {
            // Arrange
            var now = DateTime.UtcNow;
            var futureKickoff = now.AddHours(2);
            var game = _fixture.Build<Game>()
                .With(g => g.Home, "Home United")
                .With(g => g.Away, "Away City")
                .With(g => g.Start, futureKickoff)
                .With(g => g.IsInProgress, true)
                .With(g => g.IsFinished, true)
                .With(g => g.Minute, 0)
                .With(g => g.League, "Cup Gamma")
                .With(g => g.ApiLeague, string.Empty)
                .With(g => g.StatusText, string.Empty)
                .Create();

            // Act
            var isUpcoming = game.IsScheduledUpcoming(now);

            // Assert
            Assert.True(isUpcoming);
        }

        [Fact]
        public void ExactMatch_ResolvesKickoffFromDuplicateBbcVariant()
        {
            // Arrange
            var apiWrongStart = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);
            var bbcKickoff = new DateTime(2026, 6, 13, 1, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.Start, apiWrongStart)
                    .With(g => g.ApiLeague, "League Alpha")
                    .With(g => g.League, "League Alpha")
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.MinValue,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Alpha",
                    HasProgress = false
                },
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home Utd",
                    Away = "Away City",
                    KickoffUtc = bbcKickoff,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "home-united.svg",
                    AwayBadgeUrl = "away-city.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal(bbcKickoff, g.Start);
            Assert.Equal("Home Utd", g.BBCHome);
        }

        [Fact]
        public void FeaturedBucket_NonClubSides_KeepsBbcLeague()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Northland")
                    .With(g => g.Away, "Southria")
                    .With(g => g.ApiLeague, "League Alpha")
                    .With(g => g.League, "League Alpha")
                    .With(g => g.Start, DateTime.UtcNow)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Northland",
                    Away = "Southria",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "home.svg",
                    AwayBadgeUrl = "away.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("League Alpha", g.BBCLeague);
            Assert.Equal("League Alpha", g.League);
        }

        [Fact]
        public void FeaturedBucket_ClubSides_KeepsBbcLeague()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.ApiLeague, "League Alpha")
                    .With(g => g.League, "League Alpha")
                    .With(g => g.Start, DateTime.UtcNow)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "home.svg",
                    AwayBadgeUrl = "away.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("League Alpha", g.BBCLeague);
            Assert.Equal("League Alpha", g.League);
        }

        [Fact]
        public void FuzzyMatch_RepublicQualifier_MatchesShortName()
        {
            // Arrange
            var kickoff = new DateTime(2026, 6, 18, 23, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Southria")
                    .With(g => g.Away, "Northland Republic")
                    .With(g => g.Start, kickoff)
                    .With(g => g.ApiLeague, "League Alpha")
                    .With(g => g.League, "League Alpha")
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Southria",
                    Away = "Northland",
                    KickoffUtc = kickoff,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "southria.svg",
                    AwayBadgeUrl = "northland.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Southria", g.BBCHome);
            Assert.Equal("Northland", g.BBCAway);
            Assert.Equal("League Alpha", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_DiacriticHome_MatchesAsciiHome()
        {
            // Arrange
            var kickoff = new DateTime(2026, 6, 18, 19, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Cote Avon")
                    .With(g => g.Away, "Southria")
                    .With(g => g.Start, kickoff)
                    .With(g => g.ApiLeague, "League Alpha")
                    .With(g => g.League, "League Alpha")
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Côte Avon",
                    Away = "Southria",
                    KickoffUtc = kickoff,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "cote-avon.svg",
                    AwayBadgeUrl = "southria.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Côte Avon", g.BBCHome);
            Assert.Equal("Southria", g.BBCAway);
            Assert.Equal("League Alpha", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_AsciiHome_MatchesAsciiHome()
        {
            // Arrange
            var kickoff = new DateTime(2026, 6, 18, 19, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Cote Avon")
                    .With(g => g.Away, "Southria")
                    .With(g => g.Start, kickoff)
                    .With(g => g.ApiLeague, "League Alpha")
                    .With(g => g.League, "League Alpha")
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Cote Avon",
                    Away = "Southria",
                    KickoffUtc = kickoff,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = "cote-avon.svg",
                    AwayBadgeUrl = "southria.svg",
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Cote Avon", g.BBCHome);
            Assert.Equal("Southria", g.BBCAway);
        }

        [Fact]
        public void ApiLeague_KeepsMoreSpecificName_WhenBbcIsSubstring()
        {
            // Arrange
            var games = new List<Game>
            {
                _fixture.Build<Game>()
                    .With(g => g.Home, "Home United")
                    .With(g => g.Away, "Away City")
                    .With(g => g.ApiLeague, "Northern League Alpha")
                    .With(g => g.League, "Northern League Alpha")
                    .With(g => g.Start, DateTime.UtcNow)
                    .Create()
            };
            var bbc = new List<BbcFixture>
            {
                _fixture.Create<BbcFixture>() with
                {
                    Home = "Home United",
                    Away = "Away City",
                    KickoffUtc = DateTime.UtcNow,
                    Status = "",
                    IsFinished = false,
                    IsInProgress = false,
                    IsHalfTime = false,
                    Minute = null,
                    HomeScore = null,
                    AwayScore = null,
                    HomeBadgeUrl = string.Empty,
                    AwayBadgeUrl = string.Empty,
                    League = "League Alpha",
                    HasProgress = false
                }
            };
            var matcher = _fixture.Create<GameMatcher>();

            // Act
            matcher.EnrichGames(games, bbc, "Northern League Alpha");

            // Assert
            var g = games.First();
            Assert.Equal("Northern League Alpha", g.BBCLeague);
            Assert.Equal("Northern League Alpha", g.League);
        }
    }
}
