using System.Net;
using VardyParty.Ports;

namespace VardyParty.Hosting;

/// <summary>
/// The DoH address <see cref="CloudflareDnsOverHttpsClient"/> connects to.
/// </summary>
public sealed class CloudflareDnsOverHttpsEndpoint : IDnsOverHttpsEndpoint
{
    public IPAddress Address => CloudflareDnsOverHttpsClient.ResolverAddress;
}
