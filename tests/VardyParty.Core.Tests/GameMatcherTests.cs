using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using VardyParty.Models;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class GameMatcherTests
    {
        [Fact]
        public void ExactMatch_EnrichesProperties_InProgress()
        {
            var games = new List<Game> { new Game { Home = "Team A", Away = "Team B" } };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Team A", "Team B", DateTime.UtcNow, "Live", false, true, false, 22, 0, 10, "homebadge", "awaybadge", "League", true)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "League");

            var g = games.First();
            Assert.Equal(0, g.HomeScore);
            Assert.Equal(10, g.AwayScore);
            Assert.Equal(22, g.Minute);
            Assert.True(g.IsInProgress);
            Assert.False(g.IsFinished);
            Assert.Equal("homebadge", g.HomeBadgeUrl);
            Assert.Equal("League", g.BBCLeague);
        }

        [Fact]
        public void ExactMatch_EnrichesProperties_Finished()
        {
            var games = new List<Game> { new Game { Home = "Team A", Away = "Team B" } };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Team A", "Team B", DateTime.UtcNow, "Live", true, false, false, null, 0, 10, "homebadge", "awaybadge", "League", true)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "League");

            var g = games.First();
            Assert.Equal(0, g.HomeScore);
            Assert.Equal(10, g.AwayScore);
            Assert.Null(g.Minute);
            Assert.False(g.IsInProgress);
            Assert.True(g.IsFinished);
            Assert.Equal("homebadge", g.HomeBadgeUrl);
            Assert.Equal("League", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_Acronym_Matches()
        {
            var games = new List<Game> { new Game { Home = "PSG", Away = "Marseille" } };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Paris Saint-Germain", "Marseille", DateTime.UtcNow, "", false, false, false, null, null, null, string.Empty, string.Empty, "Ligue 1", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>. Instance);
            matcher.EnrichGames(games, bbc, "Ligue 1");

            var g = games.First();
            Assert.Equal("Paris Saint-Germain", g.BBCHome);
            Assert.Equal("Ligue 1", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_ManUnited_To_ManchesterUnited_Brighton()
        {
            var games = new List<Game> { new Game { Home = "Man United", Away = "Brighton" } };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Manchester United", "Brighton & Hove Albion", DateTime.UtcNow, "", false, false, false, null, null, null, string.Empty, string.Empty, "Premier League", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Premier League");

            var g = games.First();
            Assert.Equal("Manchester United", g.BBCHome);
            Assert.Equal("Brighton & Hove Albion", g.BBCAway);
            Assert.Equal("Premier League", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_AlHazm_Matches_AlHazem()
        {
            // Verifies fix for issue where "Al" was treated as a stopword, causing short names like "Hazm" vs "Hazem"
            // to have disproportionately high Levenshtein penalties.
            
            // API: "Al Hazm" v " Al Najma" (Note the leading space in API Away team to simulate data issues)
            var games = new List<Game> { new Game { Home = "Al Hazm", Away = " Al Najma" } };

            // BBC: "Al Hazem" v "Al Najma"
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Al Hazem", "Al Najma", DateTime.UtcNow, "", false, false, false, null, null, null, string.Empty, string.Empty, "Saudi League", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Saudi League");

            var g = games.First();
            // If matched, enriched fields like BBCLeague (and potentially others) will be populated
            Assert.Equal("Al Hazem", g.BBCHome);
            Assert.Equal("Al Najma", g.BBCAway);
            Assert.Equal("Saudi League", g.BBCLeague);
        }

        [Fact]
        public void EnrichGame_PostponedStatus_MapsToGameStatus()
        {
            // Arrange
            var games = new List<Game> { new Game { Home = "Team A", Away = "Team B" } };
            // BbcFixture with Status="Postponed" and HasProgress=false (as per BbcFixturesService logic)
            // Constructor: (Home, Away, Start, Status, IsFinished, IsInProgress, IsHalfTime, Minute, HomeScore, AwayScore, HomeBadge, AwayBadge, League, HasProgress, AfterExtraTime, PenWinner, PenWinGoals, PenLoseGoals)
            // Note: BbcFixture constructor signature might vary, checking GameMatcherTests.cs usage:
            // new BbcFixture("Team A", "Team B", DateTime.UtcNow, "Live", false, true, false, 22, 0, 10, "homebadge", "awaybadge", "League", true)
            // It seems the constructor has many arguments. I will try to match the one used in other tests but adapting for Postponed.
            
            var bbc = new List<BbcFixture>
            {
                new BbcFixture(
                    "Team A", 
                    "Team B", 
                    DateTime.UtcNow, 
                    "Postponed", // Status
                    false, // IsFinished
                    false, // IsInProgress
                    false, // IsHalfTime
                    null, // Minute
                    null, // HomeScore
                    null, // AwayScore
                    "homebadge", 
                    "awaybadge", 
                    "League", 
                    false, // HasProgress - IMPORTANT: Service sets this to false for postponed
                    false, // AfterExtraTime
                    string.Empty, // PenWinner
                    null, // PenWinGoals
                    null // PenLoseGoals
                )
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);

            // Act
            matcher.EnrichGames(games, bbc, "League");

            // Assert
            var g = games.First();
            Assert.Equal("Team A", g.BBCHome); // Verify match occurred
            Assert.Equal("Postponed", g.StatusText);
            // Also check boolean flags if any
            Assert.True(g.IsPostponed, "Game.IsPostponed should be true");
        }

        [Fact]
        public void FuzzyMatch_IstanbulBasaksehir_Matches()
        {
            // API: "Istanbul Basaksehir" v "Fatih Karagumruk"
            var games = new List<Game> { new Game { Home = "Istanbul Basaksehir", Away = "Fatih Karagumruk" } };

            // BBC: "?stanbul Ba?ak?ehir" v "Fatih Karagümrük"
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("İstanbul Başakşehir", "Fatih Karagümrük", DateTime.UtcNow, "", false, false, false, null, null, null, string.Empty, string.Empty, "Turkish Super Lig", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Turkish Super Lig");

            var g = games.First();
            Assert.Equal("İstanbul Başakşehir", g.BBCHome);
            Assert.Equal("Fatih Karagümrük", g.BBCAway);
            Assert.Equal("Turkish Super Lig", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_Cagilari_Matches_Cagliari()
        {
            // API: "Cagilari" (Typo) v "Juventus"
            var games = new List<Game> { new Game { Home = "Cagilari", Away = "Juventus" } };

            // BBC: "Cagliari" v "Juventus"
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Cagliari", "Juventus", DateTime.UtcNow, "", false, false, false, null, null, null, string.Empty, string.Empty, "Serie A", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Serie A");

            var g = games.First();
            Assert.Equal("Cagliari", g.BBCHome);
            Assert.Equal("Juventus", g.BBCAway);
            Assert.Equal("Serie A", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_AtleticoMadrid_DeportivoAlaves_Matches_Alaves()
        {
            // API: "Atletico Madrid" v "Deportivo Alaves"
            var games = new List<Game> { new Game { Home = "Atletico Madrid", Away = "Deportivo Alaves" } };

            // BBC: "Atletico Madrid" v "Alavés"
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Atletico Madrid", "Alavés", DateTime.UtcNow, "", false, false, false, null, null, null, string.Empty, string.Empty, "La Liga", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "La Liga");

            var g = games.First();
            Assert.Equal("Atletico Madrid", g.BBCHome);
            Assert.Equal("Alavés", g.BBCAway);
            Assert.Equal("La Liga", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_Besiktas_Matches_Besiktas_WithTurkishCharacter()
        {
            // API: "Besiktas" v "Kayserispor" (Latin characters)
            var games = new List<Game> { new Game { Home = "Besiktas", Away = "Kayserispor" } };

            // BBC: "Beşiktaş" v "Kayserispor" (Turkish ş character)
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Beşiktaş", "Kayserispor", DateTime.UtcNow, "", false, false, false, null, null, null, string.Empty, string.Empty, "Turkish Super Lig", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Turkish Super Lig");

            var g = games.First();
            Assert.Equal("Beşiktaş", g.BBCHome);
            Assert.Equal("Kayserispor", g.BBCAway);
            Assert.Equal("Turkish Super Lig", g.BBCLeague);
        }

        [Fact]
        public void ExactMatch_Lazio_Como_EnrichesWithBadgeUrls()
        {
            // API Game: Lazio vs Como from footybite.to
            var apiStartTime = new DateTime(2026, 1, 19, 19, 45, 0, DateTimeKind.Utc);
            var games = new List<Game> 
            { 
                new Game 
                { 
                    Home = "Lazio", 
                    Away = "Como",
                    Start = apiStartTime,
                    League = "Serie A"
                } 
            };

            // BBC Fixture: Lazio vs Como with badge URLs from BBC Sport
            var bbcKickoff = new DateTime(2026, 1, 19, 19, 45, 0, DateTimeKind.Utc);
            var bbc = new List<BbcFixture>
            {
                new BbcFixture(
                    "Lazio", 
                    "Como", 
                    bbcKickoff, 
                    "", // Status
                    false, // IsFinished
                    false, // IsInProgress
                    false, // IsHalfTime
                    null, // Minute
                    null, // HomeScore
                    null, // AwayScore
                    "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/lazio.8fc1f19371.svg", // HomeBadgeUrl
                    "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/como.57ce7c985f.webp", // AwayBadgeUrl
                    "Serie A", 
                    false // HasProgress
                )
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Serie A");

            var g = games.First();
            
            // Verify exact match occurred
            Assert.Equal("Lazio", g.BBCHome);
            Assert.Equal("Como", g.BBCAway);
            Assert.Equal("Serie A", g.BBCLeague);
            
            // Verify badge URLs were enriched from BBC fixture
            Assert.Equal("https://static.files.bbci.co.uk/core/website/assets/static/sport/football/lazio.8fc1f19371.svg", g.HomeBadgeUrl);
            Assert.Equal("https://static.files.bbci.co.uk/core/website/assets/static/sport/football/como.57ce7c985f.webp", g.AwayBadgeUrl);
        }

        [Fact]
        public void FuzzyMatch_Kobenhavn_Matches_Copenhagen()
        {
            // API Game: "Kobenhavn" vs "Napoli" (Danish club name in native spelling)
            var apiStartTime = new DateTime(2026, 1, 20, 20, 0, 0, DateTimeKind.Utc);
            var games = new List<Game> 
            { 
                new Game 
                { 
                    Home = "Kobenhavn", 
                    Away = "Napoli",
                    Start = apiStartTime,
                    League = "UEFA Champions League"
                } 
            };

            // BBC Fixture: "Copenhagen" vs "Napoli" (English club name)
            var bbc = new List<BbcFixture>
            {
                new BbcFixture(
                    "Copenhagen", 
                    "Napoli", 
                    apiStartTime, 
                    "20:00", 
                    false, 
                    false, 
                    false, 
                    null, 
                    null, 
                    null,
                    "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/fc-copenhagen.476d1e3526.svg",
                    "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/napoli.29b133b9ff.svg",
                    "UEFA Champions League",
                    false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "UEFA Champions League");

            var g = games.First();
            
            // Verify fuzzy match occurred
            Assert.Equal("Copenhagen", g.BBCHome);
            Assert.Equal("Napoli", g.BBCAway);
            Assert.Equal("UEFA Champions League", g.BBCLeague);
            
            // Verify badge URLs were enriched from BBC fixture
            Assert.Equal("https://static.files.bbci.co.uk/core/website/assets/static/sport/football/fc-copenhagen.476d1e3526.svg", g.HomeBadgeUrl);
            Assert.Equal("https://static.files.bbci.co.uk/core/website/assets/static/sport/football/napoli.29b133b9ff.svg", g.AwayBadgeUrl);
        }

        [Fact]
        public void FuzzyMatch_Internazionale_Matches_InterMilan()
        {
            // API Game: "Internazionale" vs "Arsenal" (Italian full club name)
            var apiStartTime = new DateTime(2026, 1, 20, 20, 0, 0, DateTimeKind.Utc);
            var games = new List<Game> 
            { 
                new Game 
                { 
                    Home = "Internazionale", 
                    Away = "Arsenal",
                    Start = apiStartTime,
                    League = "UEFA Champions League"
                } 
            };

            // BBC Fixture: "Inter Milan" vs "Arsenal" (English/shortened club name)
            var bbc = new List<BbcFixture>
            {
                new BbcFixture(
                    "Inter Milan", 
                    "Arsenal", 
                    apiStartTime, 
                    "20:00", 
                    false, 
                    false, 
                    false, 
                    null, 
                    null, 
                    null,
                    "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/inter-milan.209b8285b0.svg",
                    "https://static.files.bbci.co.uk/core/website/assets/static/sport/football/arsenal.5be7ff54ce.svg",
                    "UEFA Champions League",
                    false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "UEFA Champions League");

            var g = games.First();
            
            // Verify fuzzy match occurred
            Assert.Equal("Inter Milan", g.BBCHome);
            Assert.Equal("Arsenal", g.BBCAway);
            Assert.Equal("UEFA Champions League", g.BBCLeague);
            
            // Verify badge URLs were enriched from BBC fixture
            Assert.Equal("https://static.files.bbci.co.uk/core/website/assets/static/sport/football/inter-milan.209b8285b0.svg", g.HomeBadgeUrl);
            Assert.Equal("https://static.files.bbci.co.uk/core/website/assets/static/sport/football/arsenal.5be7ff54ce.svg", g.AwayBadgeUrl);
        }

        [Fact]
        public void ExactMatch_EnrichesAggregateScores()
        {
            var games = new List<Game> { new Game { Home = "Team A", Away = "Team B" } };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture(
                    "Team A",
                    "Team B",
                    DateTime.UtcNow,
                    "FT",
                    true,
                    false,
                    false,
                    null,
                    1,
                    0,
                    "",
                    "",
                    "League",
                    true,
                    false,
                    "",
                    null,
                    null,
                    5,
                    3)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "League");

            var g = games.First();
            Assert.Equal(5, g.AggregateHomeScore);
            Assert.Equal(3, g.AggregateAwayScore);
            Assert.Equal(1, g.HomeScore);
            Assert.Equal(0, g.AwayScore);
        }

        [Fact]
        public void FuzzyMatch_Usa_Matches_UnitedStates()
        {
            var apiWrongStart = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);
            var bbcKickoff = new DateTime(2026, 6, 13, 1, 0, 0, DateTimeKind.Utc); // 02:00 BST on 13 June
            var games = new List<Game>
            {
                new Game { Home = "USA", Away = "Paraguay", Start = apiWrongStart, ApiLeague = "Important Games", League = "Important Games" }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("United States", "Paraguay", bbcKickoff, "", false, false, false, null,
                    null, null, "usa.svg", "paraguay.svg", "Important Games", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Important Games");

            var g = games.First();
            Assert.Equal("United States", g.BBCHome);
            Assert.Equal("FIFA World Cup", g.BBCLeague);
            Assert.Equal(bbcKickoff, g.Start);
        }

        [Fact]
        public void FutureKickoff_WithStaleMinuteZero_IsScheduledUpcoming()
        {
            var now = DateTime.UtcNow;
            var futureKickoff = now.AddHours(2);
            var games = new List<Game>
            {
                new Game
                {
                    Home = "USA",
                    Away = "Paraguay",
                    Start = futureKickoff,
                    Minute = 0,
                    IsInProgress = true,
                    IsFinished = true,
                    League = "FIFA World Cup"
                }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("United States", "Paraguay", futureKickoff, "", false, false, false, null,
                    null, null, "usa.svg", "paraguay.svg", "Important Games", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Important Games");

            var g = games.First();
            Assert.False(g.IsInProgress);
            Assert.False(g.IsFinished);
            Assert.Null(g.Minute);
            Assert.True(g.IsScheduledUpcoming(now));
        }

        [Fact]
        public void IsScheduledUpcoming_FutureKickoff_IgnoresStaleInProgressFlag()
        {
            var now = DateTime.UtcNow;
            var futureKickoff = now.AddHours(2);
            var game = new Game
            {
                Home = "USA",
                Away = "Paraguay",
                Start = futureKickoff,
                IsInProgress = true,
                IsFinished = true,
                Minute = 0,
                League = "FIFA World Cup"
            };

            Assert.True(game.IsScheduledUpcoming(now));
        }

        [Fact]
        public void ExactMatch_Usa_ResolvesKickoffFromDuplicateBbcVariant()
        {
            var apiWrongStart = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);
            var bbcKickoff = new DateTime(2026, 6, 13, 1, 0, 0, DateTimeKind.Utc); // 02:00 BST on 13 June
            var games = new List<Game>
            {
                new Game { Home = "USA", Away = "Paraguay", Start = apiWrongStart, ApiLeague = "Important Games", League = "Important Games" }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("USA", "Paraguay", DateTime.MinValue, "", false, false, false, null,
                    null, null, string.Empty, string.Empty, "Important Games", false),
                new BbcFixture("United States", "Paraguay", bbcKickoff, "", false, false, false, null,
                    null, null, "usa.svg", "paraguay.svg", "Important Games", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Important Games");

            var g = games.First();
            Assert.Equal(bbcKickoff, g.Start);
            Assert.Equal("United States", g.BBCHome);
        }

        [Fact]
        public void ImportantGames_InternationalFixture_MapsToFifaWorldCup()
        {
            var games = new List<Game>
            {
                new Game
                {
                    Home = "Canada",
                    Away = "Bosnia and Herzegovina",
                    ApiLeague = "Important Games",
                    League = "Important Games",
                    Start = DateTime.UtcNow
                }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Canada", "Bosnia and Herzegovina", DateTime.UtcNow, "", false, false, false, null,
                    null, null, "home.svg", "away.svg", "Important Games", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Important Games");

            var g = games.First();
            Assert.Equal("FIFA World Cup", g.BBCLeague);
            Assert.Equal("FIFA World Cup", g.League);
        }

        [Fact]
        public void ImportantGames_ClubFixture_KeepsBbcLeague()
        {
            var games = new List<Game>
            {
                new Game
                {
                    Home = "Real Madrid",
                    Away = "Barcelona",
                    ApiLeague = "Important Games",
                    League = "Important Games",
                    Start = DateTime.UtcNow
                }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Real Madrid", "Barcelona", DateTime.UtcNow, "", false, false, false, null,
                    null, null, "home.svg", "away.svg", "Important Games", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Important Games");

            var g = games.First();
            Assert.Equal("Important Games", g.BBCLeague);
            Assert.Equal("Important Games", g.League);
        }

        [Fact]
        public void FuzzyMatch_KoreaRepublic_Matches_SouthKorea()
        {
            var kickoff = new DateTime(2026, 6, 18, 23, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                new Game { Home = "Mexico", Away = "Korea Republic", Start = kickoff, ApiLeague = "Important Games", League = "Important Games" }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Mexico", "South Korea", kickoff, "", false, false, false, null,
                    null, null, "mexico.svg", "south-korea.svg", "Important Games", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Important Games");

            var g = games.First();
            Assert.Equal("Mexico", g.BBCHome);
            Assert.Equal("South Korea", g.BBCAway);
            Assert.Equal("FIFA World Cup", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_IvoryCoast_Matches_CoteDIvoire()
        {
            var kickoff = new DateTime(2026, 6, 18, 19, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                new Game { Home = "Ivory Coast", Away = "Ecuador", Start = kickoff, ApiLeague = "Important Games", League = "Important Games" }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Côte d'Ivoire", "Ecuador", kickoff, "", false, false, false, null,
                    null, null, "ivory-coast.svg", "ecuador.svg", "Important Games", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Important Games");

            var g = games.First();
            Assert.Equal("Côte d'Ivoire", g.BBCHome);
            Assert.Equal("Ecuador", g.BBCAway);
            Assert.Equal("FIFA World Cup", g.BBCLeague);
        }

        [Fact]
        public void FuzzyMatch_IvoryCoast_Matches_CoteDIvoire_WithoutDiacritics()
        {
            var kickoff = new DateTime(2026, 6, 18, 19, 0, 0, DateTimeKind.Utc);
            var games = new List<Game>
            {
                new Game { Home = "Ivory Coast", Away = "Ecuador", Start = kickoff, ApiLeague = "Important Games", League = "Important Games" }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Cote d'Ivoire", "Ecuador", kickoff, "", false, false, false, null,
                    null, null, "ivory-coast.svg", "ecuador.svg", "Important Games", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Important Games");

            var g = games.First();
            Assert.Equal("Cote d'Ivoire", g.BBCHome);
            Assert.Equal("Ecuador", g.BBCAway);
        }

        [Fact]
        public void LebanesePremierLeague_KeepsApiLeague_WhenBbcWouldBeLessSpecific()
        {
            var games = new List<Game>
            {
                new Game
                {
                    Home = "Al Nejmeh",
                    Away = "Al Ahed",
                    ApiLeague = "Lebanese Premier League",
                    League = "Lebanese Premier League",
                    Start = DateTime.UtcNow
                }
            };
            var bbc = new List<BbcFixture>
            {
                new BbcFixture("Al Nejmeh", "Al Ahed", DateTime.UtcNow, "", false, false, false, null,
                    null, null, string.Empty, string.Empty, "Premier League", false)
            };

            var matcher = new GameMatcher(NullLogger<GameMatcher>.Instance);
            matcher.EnrichGames(games, bbc, "Lebanese Premier League");

            var g = games.First();
            Assert.Equal("Lebanese Premier League", g.BBCLeague);
            Assert.Equal("Lebanese Premier League", g.League);
        }
    }
}
