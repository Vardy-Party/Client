using VardyParty.Kernel;

namespace VardyParty.Desktop.Services;

/// <summary>
/// Fabricated catalog for demos and headless smoke tests
/// (VARDYPARTY_DESKTOP_SAMPLE_DATA=1). Fictional teams; covers the status
/// spectrum the card renders: live minutes with stoppage time, half time,
/// extra time, penalties, aggregates, upcoming today/tomorrow, postponed.
/// </summary>
public static class SampleGames
{
    public static Dictionary<string, List<Game>> Build()
    {
        var now = DateTime.UtcNow;

        return new Dictionary<string, List<Game>>
        {
            ["League Alpha"] = new()
            {
                new Game
                {
                    Href = "sample/alpha-1",
                    Home = "Home United", Away = "Away City",
                    League = "League Alpha",
                    Start = now.AddMinutes(-47),
                    IsInProgress = true,
                    Minute = 4502, // 45+2'
                    HomeScore = 2, AwayScore = 1,
                    StatusText = "45+2'",
                },
                new Game
                {
                    Href = "sample/alpha-2",
                    Home = "North Rovers", Away = "South Wanderers",
                    League = "League Alpha",
                    Start = now.AddMinutes(-60),
                    IsHalfTime = true,
                    HomeScore = 0, AwayScore = 0,
                    StatusText = "HT",
                },
                new Game
                {
                    Href = "sample/alpha-3",
                    Home = "East Athletic", Away = "West Albion",
                    League = "League Alpha",
                    Start = now.AddHours(3),
                },
            },
            ["Cup Beta"] = new()
            {
                new Game
                {
                    Href = "sample/beta-1",
                    Home = "River Town", Away = "Lake Borough",
                    League = "Cup Beta",
                    Start = now.AddMinutes(-125),
                    IsInProgress = true,
                    HomeScore = 1, AwayScore = 1,
                    AggregateHomeScore = 2, AggregateAwayScore = 2,
                    StatusText = "Extra time 98'",
                },
                new Game
                {
                    Href = "sample/beta-2",
                    Home = "Harbour FC", Away = "Valley SC",
                    League = "Cup Beta",
                    Start = now.AddMinutes(-140),
                    IsInProgress = true,
                    HomeScore = 2, AwayScore = 2,
                    AggregateHomeScore = 3, AggregateAwayScore = 3,
                    StatusText = "Penalties",
                },
            },
            ["League Gamma"] = new()
            {
                new Game
                {
                    Href = "sample/gamma-1",
                    Home = "Forest Green Sample", Away = "Mountain Grey",
                    League = "League Gamma",
                    Start = now.AddDays(1).AddHours(2),
                },
                new Game
                {
                    Href = "sample/gamma-2",
                    Home = "Coast Rangers", Away = "Plains County",
                    League = "League Gamma",
                    Start = now.AddHours(6),
                    StatusText = "Postponed",
                },
            },
        };
    }
}
