namespace VardyParty.Ports;

/// <summary>
/// Identifies this app process to the local-service. The phone and the PC
/// share an Auth0 subject, so a browser cannot be keyed by that subject.
/// One id is created when the process starts and sent on every local-service call.
/// </summary>
public sealed class LocalServiceConnection
{
    public const string HeaderName = "X-Vardy-Connection";

    public string Id { get; } = Guid.NewGuid().ToString("N");
}
