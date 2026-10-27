using VardyParty.Kernel;
using VardyParty.Ports;

namespace VardyParty.Playback;

public delegate Task<string?> ResolveFreshPlaybackUrlAsync(
    EnrichedStream stream,
    CancellationToken cancellationToken);

/// <summary>
/// Pool + fresh-resolve actions for every host (Android, Windows, Linux, iOS, MacCatalyst).
/// Lives in Playback so OS players do not each reimplement retry/accept. Playback does not
/// reference <c>IApiService</c>; hosts pass a resolve delegate.
/// Fresh URLs are accepted only via <see cref="PlaybackPolicy.ShouldAcceptFreshM3U8"/> against
/// <see cref="PlaybackSessionController.Snapshot"/> current URL — not a host-local field.
/// </summary>
public sealed class PlaybackPoolCommandActions(
    PlaybackSessionController session,
    IStreamSwitchingService switching,
    ResolveFreshPlaybackUrlAsync? resolveFresh,
    Action<string, bool, bool> attachViaSession,
    Action<PlaybackCommand> applyCommand)
{
    public void ClearCurrentResolvedUrl()
    {
        var failed = switching.GetCurrentStream();
        if (failed != null)
            failed.ResolvedM3U8Url = null;
    }

    public void RemoveCurrentFromPool() => switching.RemoveCurrentStream();

    public void SyncHealthyStreamCount()
        => session.SetHealthyStreamCount(switching.GetHealthyStreams().Count);

    public void SwitchPoolToPrevious() => switching.SwitchToPreviousStream();

    public async Task AttachCurrentFromPoolAsync()
    {
        var current = switching.GetCurrentStream();
        if (current == null)
            return;

        // FB/V1 soak tokens are one-shot. Find/prefetch ResolvedM3U8Url is often
        // already burned — always /play again when a resolver is wired. Fall back
        // to the cached URL only when fresh resolve fails (and mark usedCachedUrl
        // so ShouldRetryFreshResolve can still fire once).
        string? url = null;
        var usedCachedUrl = false;
        if (current.Stream != null && resolveFresh != null)
        {
            try
            {
                url = await resolveFresh(current, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(url))
                    current.ResolvedM3U8Url = url;
            }
            catch
            {
                // Fall through to cached URL below.
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            url = current.ResolvedM3U8Url;
            usedCachedUrl = !string.IsNullOrWhiteSpace(url);
        }

        if (string.IsNullOrWhiteSpace(url))
            return;

        attachViaSession(url, usedCachedUrl, true);
    }

    public async Task RetryFreshResolveAsync()
    {
        try
        {
            var current = switching.GetCurrentStream();
            var fresh = current != null && resolveFresh != null
                ? await resolveFresh(current, CancellationToken.None)
                : null;
            if (string.IsNullOrWhiteSpace(fresh) ||
                !PlaybackPolicy.ShouldAcceptFreshM3U8(session.Snapshot.CurrentUrl, fresh))
            {
                applyCommand(PlaybackCommand.FromEffects(session.NotifyFreshResolveUnavailable()));
                return;
            }

            if (current != null)
                current.ResolvedM3U8Url = fresh;

            attachViaSession(fresh, false, true);
        }
        catch (Exception ex)
        {
            applyCommand(PlaybackCommand.FromEffects(session.NotifyFreshResolveUnavailable(ex.Message)));
        }
    }
}
