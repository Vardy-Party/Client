namespace VardyParty.Configuration;

public class BbcFixturesSettings
{
    public required int CallTimeoutSeconds { get; set; }
    public required int MaxRetries { get; set; }
    public required int RefreshSchedule { get; set; } // RefreshSchedule for BBC fixtures cache, in seconds
}
