namespace VardyParty.Hosting;

/// <summary>
/// Guest-side rules for one relayed find-streams call.
/// The phone Chrome run waits 120s for chips, then 180s for a playlist, then 60s
/// for segments (360s) on the first chip. This wait is longer so the phone does
/// not give up while that first capture is still going. Later chips stream on
/// the same session. The host HTTP client waits 630s so the harvest can finish
/// after the guest has already started playback. First chip finishes inside
/// the guest wait; the guest wait finishes inside the host HTTP timeout.
/// </summary>
internal static class RemoteComputeHttp
{
    internal static readonly TimeSpan FindingStreamsTimeout = TimeSpan.FromSeconds(420);

    internal const string StillFinding =
        "This computer is still finding a stream. Tap the game again to start a new search.";

    internal static string LocalServiceMessage(int status, string? error)
    {
        if (status == 403)
        {
            return error ?? "The remote PC refused this account";
        }

        if (status == 409)
        {
            return error?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true
                ? StillFinding
                : error ?? "The remote PC is already resolving a stream";
        }

        return error ?? $"The remote PC returned HTTP {status}";
    }
}
