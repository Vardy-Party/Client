namespace VardyParty.Streaming;

/// <summary>
/// How long the client waits for LocalService <c>GET /play</c>. Our timeout must
/// sit ABOVE LocalService's own final wait, or we give up while it is still
/// finishing and the row is deferred for nothing.
/// <para>
/// LocalService V1 <c>PlayEngine</c> (Strategies repo): page navigation
/// <c>PageGotoTimeoutMs = 30_000</c>, then the final
/// <c>WaitForSettledPlaylistReadyAsync(timeoutMs: 5_000)</c> before
/// <c>FinalWaitComplete</c>. So the service's last wait ends 35s in
/// (<see cref="LocalServiceFinalWait"/>). We add a 30s margin
/// (<see cref="Margin"/>) for the player interaction stages and the LAN hop:
/// the base timeout is at least 65s (<see cref="MinimumPlayCallTimeout"/>).
/// A configured <c>M3U8CallTimeoutSeconds</c> above that wins.
/// </para>
/// <para>
/// A stream that hit our timeout gets double the base on the next attempt,
/// capped at <see cref="MaxPlayCallTimeout"/> (120s). The slowest V1 path
/// (navigation 30s + iframe 10s + player UI 5s + two 10s clicks + settle 5s,
/// about 70s) fits inside the doubled timeout.
/// </para>
/// </summary>
public static class LocalServicePlayBudget
{
    /// <summary>LocalService page navigation timeout (PlayEngine.PageGotoTimeoutMs).</summary>
    public static readonly TimeSpan LocalServicePageGotoTimeout = TimeSpan.FromSeconds(30);

    /// <summary>LocalService final settled-playlist wait (WaitForSettledPlaylistReadyAsync timeoutMs).</summary>
    public static readonly TimeSpan LocalServiceSettledPlaylistWait = TimeSpan.FromSeconds(5);

    /// <summary>When LocalService's final wait ends: navigation plus the settled-playlist wait (35s).</summary>
    public static readonly TimeSpan LocalServiceFinalWait = LocalServicePageGotoTimeout + LocalServiceSettledPlaylistWait;

    /// <summary>Headroom above LocalService's final wait.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    /// <summary>The smallest base <c>/play</c> timeout: final wait plus margin (65s).</summary>
    public static readonly TimeSpan MinimumPlayCallTimeout = LocalServiceFinalWait + Margin;

    /// <summary>The cap for the doubled timeout after a slow <c>/play</c>.</summary>
    public static readonly TimeSpan MaxPlayCallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The base timeout: the configured value, but never below <see cref="MinimumPlayCallTimeout"/>.</summary>
    public static TimeSpan BaseTimeout(TimeSpan? configured)
    {
        var value = configured ?? TimeSpan.Zero;
        return value > MinimumPlayCallTimeout ? value : MinimumPlayCallTimeout;
    }

    /// <summary>After a timeout: double the base, capped at <see cref="MaxPlayCallTimeout"/>.</summary>
    public static TimeSpan ExtendedTimeout(TimeSpan baseTimeout)
    {
        var doubled = TimeSpan.FromTicks(baseTimeout.Ticks * 2);
        return doubled < MaxPlayCallTimeout ? doubled : MaxPlayCallTimeout;
    }
}
