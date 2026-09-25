using System.Net;

namespace VardyParty.Ports;

/// <summary>
/// The DoH resolver address the app already uses for its own lookups.
/// Local LAN play forwards this IP so the resolver browser uses the same authority.
/// </summary>
public interface IDnsOverHttpsEndpoint
{
    IPAddress Address { get; }
}
