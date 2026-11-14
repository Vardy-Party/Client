using VardyParty.Kernel;
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
}
