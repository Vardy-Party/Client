using VardyParty.Kernel;

namespace VardyParty.Streaming;

/// <summary>
/// Resolves a stream through a paired compute host when LAN discovery finds nothing.
/// DNS for the browser's traffic stays on this device.
/// </summary>
public interface IRemoteComputePlay
{
    bool IsPaired { get; }

    /// <summary>Set when the last <see cref="ResolveAsync"/> failed. Cleared on success.</summary>
    RemoteComputeFault? LastFault { get; }

    event Action<RemoteComputeFault>? Faulted;

    Task<M3U8Response?> ResolveAsync(
        bool useMp,
        string streamUrl,
        string? playerStreamName,
        CancellationToken cancellationToken = default);
}
