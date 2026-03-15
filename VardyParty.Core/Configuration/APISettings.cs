using System.Diagnostics.CodeAnalysis;

namespace VardyParty.Configuration;

public class APISettings
{
    public static string SectionName => "Api";

    [SetsRequiredMembers]
    public APISettings()
    {
        HeadlessBaseUrl = string.Empty;
    }

    public required string HeadlessBaseUrl { get; set; }
    public bool IgnoreSslCertificateErrors { get; set; }
}