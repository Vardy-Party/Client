namespace VardyParty.Configuration;

public class APISettings
{
    public static string SectionName => "Api";
    public required string HeadlessBaseUrl { get; set; }
}