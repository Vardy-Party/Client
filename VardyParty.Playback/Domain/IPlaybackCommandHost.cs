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
    void AttachCurrentAfterRemove();
    void RetryFreshResolve();
    void StopEngine();
    void CloseSession(string reason);
    void SwitchPoolToNext();
    void SwitchPoolToPrevious();
    void NotifyApplyFailed(Exception exception);
}
