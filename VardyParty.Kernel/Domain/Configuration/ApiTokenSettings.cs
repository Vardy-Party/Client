namespace VardyParty.Kernel;

public class ApiTokenSettings
{
    public static string SectionName => "ApiToken";
    public required string Token { get; set; }
}