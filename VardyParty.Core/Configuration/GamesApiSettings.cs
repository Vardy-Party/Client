namespace VardyParty.Configuration;

public class GamesApiSettings
{
    public static string SectionName => "GamesApi";
    public required int CallTimeoutSeconds { get; set; }
    public required int MaxRetries { get; set; }
    public required int RefreshSchedule { get; set; } // RefreshSchedule for games cache, in seconds
    public required int M3U8CallTimeoutSeconds { get; set; }
}
