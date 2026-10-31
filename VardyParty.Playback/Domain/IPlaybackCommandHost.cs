namespace VardyParty.Playback;

/// <summary>
/// Host actions for <see cref="PlaybackCommandExecutor"/>. Every OS host covers the same
/// flags. Shared pool/resolve work is <see cref="PlaybackPoolCommandActions"/> in this
/// assembly; LibVLC / AVPlayer / ExoPlayer / WinUI stay in the host methods.
/// </summary>
public interface IPlaybackCommandHost
{
    void BeginIndexSwitchSuppression();
    void EndIndexSwitchSuppression();
    void ClearCurrentResolvedUrl();
    void RemoveCurrentFromPool();
    void SyncHealthyStreamCount();
    void ReportFailed(string? reason);
    void ReportDeclined(string? reason);
    void ReportWorking();
    void MarkEstablished();
    void RaiseBuffering(bool isBuffering);
    void Attach(string url, bool isRevert);

    /// <summary>
    /// Before a revert attach: put the pool index back on the row that plays
    /// <paramref name="url"/> (the last-good stream), so the overlay number, Next
    /// and health reports follow what is on screen. Runs inside index-switch
    /// suppression, so it must not re-attach. Hosts call
    /// <see cref="PlaybackPoolCommandActions.RevertIndexToPlayingUrl"/>.
    /// </summary>
    void RevertPoolIndexTo(string url);
    void AttachCurrentAfterRemove();
    void RetryFreshResolve();
    void StopEngine();
    void CloseSession(string reason);
    void SwitchPoolToNext();
    void SwitchPoolToPrevious();
    void NotifyApplyFailed(Exception exception);
}
