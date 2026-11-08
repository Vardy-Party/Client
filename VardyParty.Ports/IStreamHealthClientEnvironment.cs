namespace VardyParty.Ports;

/// <summary>Non-PII client + LocalService environment for opted-in health reports.</summary>
public interface IStreamHealthClientEnvironment
{
    string? ClientOs { get; }
    string? ClientVersion { get; }
    string? LocalServiceOs { get; }
    string? LocalServiceVersion { get; }
}
