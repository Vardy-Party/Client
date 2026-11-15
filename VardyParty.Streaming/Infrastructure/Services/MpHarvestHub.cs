using System.Threading;

namespace VardyParty.Streaming;

/// <summary>
/// The app's one <c>/mp</c> lane: the running (or recently finished) harvest, its page,
/// who still wants it, and the single-flight POST gate. <see cref="LocalLanPlayService"/>
/// is a typed HttpClient, so DI makes a new instance for every consumer (the resolver,
/// <c>ApiService</c> for Next/switch fresh resolves, the prewarmer). Without one shared
/// hub each instance had its own harvest and gate: a switch or pre-resolve started a
/// second <c>POST /mp</c> for a page whose harvest was still running, and LocalService
/// sent it nothing while the first ran (TV log 13 Nov 2026, 18:40:32-18:41:06).
/// Registered as a singleton; tests that build a service by hand get their own hub.
/// </summary>
public sealed class MpHarvestHub
{
    internal readonly object Gate = new();
    internal MpHarvestSession? Harvest;
    internal string? Page;
    internal CancellationTokenSource? Cts;
    internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly Dictionary<string, int> Wants = new(StringComparer.OrdinalIgnoreCase);
    internal readonly SemaphoreSlim PostGate = new(1, 1);
}
