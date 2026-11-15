using VardyParty.Kernel;
using VardyParty.Ports;
namespace VardyParty.Playback;

/// <summary>Arms or cancels option 2 from a playback command (tested with a fake clock).</summary>
public static class SwitchBackCommands
{
    public static void Apply(
        SwitchBackSession session,
        bool enabled,
        PlaybackCommand cmd,
        SwitchBackCandidate? current,
        DateTimeOffset now,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!enabled)
            return;

        if (PlaybackPolicy.IsUserNavigationReason(cmd.Reason) || cmd.SwitchPoolToPrevious)
        {
            if (!session.IsArmed)
                return;
            var why = cmd.Reason ?? "user navigation";
            session.Cancel(why);
            log?.Invoke($"[switch-back] cancelled ({why})");
            return;
        }

        if (!cmd.SwitchPoolToNext || current is null)
            return;

        if (!IsCdnUnreachableMoveOn(cmd.Reason))
            return;

        session.Arm(current, now);
        log?.Invoke($"[switch-back] watching row=\"{current.RowName ?? "(unknown)"}\" after CDN unreachable");
    }

    public static bool IsCdnUnreachableMoveOn(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;
        if (reason.StartsWith(PlaybackCloseReasons.CdnUnreachable, StringComparison.Ordinal))
            return true;
        return PlaybackPolicy.ClassifyPlaybackError(reason) == PlaybackErrorKind.CdnUnreachable;
    }

    public static SwitchBackCandidate? FromRow(EnrichedStream? row, int? fallbackHeight = null)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.ResolvedM3U8Url))
            return null;

        var height = row.Health?.ProbedVideoHeight is > 0
            ? row.Health.ProbedVideoHeight
            : fallbackHeight is > 0 ? fallbackHeight : null;

        return new SwitchBackCandidate(
            PlaylistUrl: row.ResolvedM3U8Url,
            RowName: PlaylistReloadRowName.ForRow(row),
            IdentityKey: row.Stream?.Url,
            LastHeight: height,
            RequestHeaders: row.RequestHeaders,
            Referer: row.Referer);
    }

    /// <summary>
    /// Puts the abandoned playlist back on its pool row and switches to it. Must run
    /// on the UI thread (it drives <see cref="IStreamSwitchingService"/> and the
    /// attach); <see cref="SwitchBackRunner.Create"/> always dispatches it there.
    /// Returns false when the row is no longer in the pool.
    /// </summary>
    public static bool RestoreAndSwitchTo(IStreamSwitchingService? switching, SwitchBackCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (switching is null)
            return false;

        var rows = switching.GetHealthyStreams();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var catalog = row.Stream?.Url;
            var match = (!string.IsNullOrWhiteSpace(candidate.IdentityKey)
                    && string.Equals(catalog, candidate.IdentityKey, StringComparison.OrdinalIgnoreCase))
                || string.Equals(row.ResolvedM3U8Url, candidate.PlaylistUrl, StringComparison.OrdinalIgnoreCase);
            if (!match)
                continue;

            row.ResolvedM3U8Url = candidate.PlaylistUrl;
            row.UnusedResolvedUrl = true;
            return switching.SwitchToStream(i);
        }

        return false;
    }
}
