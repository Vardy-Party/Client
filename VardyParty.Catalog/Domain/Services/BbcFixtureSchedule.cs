namespace VardyParty.Services;

public static class BbcFixtureSchedule
{
    private static readonly TimeZoneInfo UkTimeZone = ResolveUkTimeZone();

    /// <summary>
    /// World Cup sessions often run until ~02:00 UK. The last kickoff of "match day N" can
    /// appear on the BBC page for calendar day N+1; when browsing at 11:00 on day N-1 we
    /// still need that page in the look-ahead window.
    /// </summary>
    private const int LateNightCutoffHour = 2;

    /// <summary>
    /// BBC fixture pages to fetch for the current rolling window (UK calendar dates).
    /// </summary>
    public static IReadOnlyList<DateOnly> GetRollingWindowPageDates(DateTime utcNow)
    {
        var ukNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, UkTimeZone);
        var ukToday = DateOnly.FromDateTime(ukNow);

        var startDate = ukNow.TimeOfDay < TimeSpan.FromHours(LateNightCutoffHour)
            ? ukToday.AddDays(-1)
            : ukToday;

        // Through 02:00 UK on the calendar day after tomorrow (inclusive page coverage).
        var endDate = ukToday.AddDays(2);

        var dates = new List<DateOnly>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            dates.Add(date);
        }

        return dates;
    }

    /// <summary>
    /// UTC instant for the end of the client look-ahead window (02:00 UK on day after tomorrow).
    /// </summary>
    public static DateTime GetLookAheadEndUtc(DateTime utcNow)
    {
        var ukNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, UkTimeZone);
        var ukToday = DateOnly.FromDateTime(ukNow);
        var endLocal = ukToday.AddDays(2).ToDateTime(new TimeOnly(LateNightCutoffHour, 0), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(endLocal, UkTimeZone);
    }

    public static bool IsWithinLookAheadWindow(DateTime kickoffUtc, DateTime utcNow)
    {
        if (kickoffUtc == default) return true;

        var kickoff = kickoffUtc.Kind == DateTimeKind.Utc ? kickoffUtc : kickoffUtc.ToUniversalTime();
        return kickoff <= GetLookAheadEndUtc(utcNow);
    }

    private static TimeZoneInfo ResolveUkTimeZone()
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
