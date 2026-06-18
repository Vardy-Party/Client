namespace VardyParty.Configuration;

public class ApiTokenSettings
{
    public static string SectionName => "ApiToken";
    public required string Token { get; set; }
}