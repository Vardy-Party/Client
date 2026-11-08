namespace VardyParty.Ports;

public sealed class NullStreamHealthClientEnvironment : IStreamHealthClientEnvironment
{
    public string? ClientOs => null;
    public string? ClientVersion => null;
    public string? LocalServiceOs => null;
    public string? LocalServiceVersion => null;
}