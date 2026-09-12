using VardyParty.Catalog;
using VardyParty.Kernel;

namespace VardyParty.Presentation;

public enum PlaybackReportUiState
{
    Idle,
    Reporting,
    Succeeded,
    Failed,
    Unavailable
}

public sealed record StreamToastModel(int Index, int Total, string? VerticalResolutionLabel)
{
    public string Text => PlayerOverlayFormatter.FormatStreamToast(Index, Total, VerticalResolutionLabel);
}

/// <summary>
/// Toolkit-free playback chrome state machine shared by Android, Windows, and
/// Linux hosts. Owns menu / video-info / stream-toast / scores visibility,
/// scores mode cycling, report status, dismiss-layer order, and command
/// entry points. Hosts bind widgets, push overlay/pool facts, and run
/// dispatcher timers.
/// </summary>
public sealed class PlaybackChromePresenter
{
    public static readonly TimeSpan StreamToastAutoHide = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReportStatusLinger = TimeSpan.FromMilliseconds(900);

    private readonly Func<string, CancellationToken, Task>? _reportBadStream;
    private readonly Func<Task>? _requestNext;
    private readonly Func<Task>? _requestPrevious;
    private readonly Action? _cleanupPool;

    public PlaybackChromePresenter(
        Func<string, CancellationToken, Task>? reportBadStream = null,
        Func<Task>? requestNext = null,
        Func<Task>? requestPrevious = null,
        Action? cleanupPool = null)
    {
        _reportBadStream = reportBadStream;
        _requestNext = requestNext;
        _requestPrevious = requestPrevious;
        _cleanupPool = cleanupPool;
    }

    public bool IsMenuVisible { get; private set; }
    public bool IsVideoInfoVisible { get; private set; }
    public bool IsStreamToastVisible { get; private set; }
    public bool IsScoresVisible { get; private set; }
    public bool OverlayLocked => IsVideoInfoVisible;
    public ScoresTickerMode ScoresMode { get; private set; } = ScoresTickerMode.SameLeagueInPlay;
    public bool CanGoNext { get; private set; }
    public PlayerOverlayInfo? OverlayInfo { get; private set; }
    public StreamToastModel? StreamToast { get; private set; }
    public PlaybackReportUiState ReportState { get; private set; } = PlaybackReportUiState.Idle;
    public string ReportStatusText { get; private set; } = string.Empty;

    public event EventHandler? StateChanged;
    public event EventHandler<StreamToastModel>? StreamToastRequested;
    public event EventHandler? StreamToastDismissed;
    public event EventHandler? ExitRequested;
    public event EventHandler? NextStreamRequested;
    public event EventHandler? PreviousStreamRequested;

    public void ApplyOverlayInfo(PlayerOverlayInfo? info)
    {
        OverlayInfo = info;
        if (info is null)
        {
            CanGoNext = false;
            RaiseStateChanged();
            return;
        }

        CanGoNext = info.Total > 1;
        var vertical = PlayerOverlayFormatter.ExtractVerticalResolutionLabel(info.Resolution);
        var toast = new StreamToastModel(info.Index, info.Total, vertical);
        var changed = StreamToast is null
            || StreamToast.Index != toast.Index
            || StreamToast.Total != toast.Total
            || !string.Equals(StreamToast.VerticalResolutionLabel, toast.VerticalResolutionLabel, StringComparison.Ordinal);

        StreamToast = toast;

        if (changed && info.Total > 0 && !IsVideoInfoVisible)
        {
            IsStreamToastVisible = true;
            StreamToastRequested?.Invoke(this, toast);
        }

        RaiseStateChanged();
    }

    public void NotifyHealthyCount(int total)
    {
        CanGoNext = total > 1;
        RaiseStateChanged();
    }

    public void ToggleMenu()
    {
        IsMenuVisible = !IsMenuVisible;
        RaiseStateChanged();
    }

    public void HideMenu()
    {
        if (!IsMenuVisible) return;
        IsMenuVisible = false;
        RaiseStateChanged();
    }

    public void ShowVideoInfo()
    {
        HideMenu();
        DismissStreamToast();
        IsVideoInfoVisible = true;
        RaiseStateChanged();
    }

    public void HideVideoInfo()
    {
        if (!IsVideoInfoVisible) return;
        IsVideoInfoVisible = false;
        RaiseStateChanged();
    }

    public void ToggleVideoInfo()
    {
        if (IsVideoInfoVisible) HideVideoInfo();
        else ShowVideoInfo();
    }

    public void ToggleScores()
    {
        HideMenu();
        if (IsScoresVisible)
        {
            IsScoresVisible = false;
        }
        else
        {
            ScoresMode = ScoresTickerMode.SameLeagueInPlay;
            IsScoresVisible = true;
        }

        RaiseStateChanged();
    }

    public void CycleScoresMode()
    {
        if (!IsScoresVisible) return;
        ScoresMode = ScoresTickerPolicy.Next(ScoresMode);
        RaiseStateChanged();
    }

    public async Task ReportBadStreamAsync(CancellationToken ct = default)
    {
        HideMenu();
        if (_reportBadStream is null)
        {
            ReportState = PlaybackReportUiState.Unavailable;
            ReportStatusText = "Report unavailable";
            RaiseStateChanged();
            return;
        }

        ReportState = PlaybackReportUiState.Reporting;
        ReportStatusText = "Reporting stream...";
        RaiseStateChanged();

        try
        {
            await _reportBadStream("User reported bad stream", ct).ConfigureAwait(false);
            ReportState = PlaybackReportUiState.Succeeded;
            ReportStatusText = "Stream reported";
        }
        catch
        {
            ReportState = PlaybackReportUiState.Failed;
            ReportStatusText = "Report failed";
        }

        RaiseStateChanged();
    }

    public void ClearReportStatus()
    {
        if (ReportState == PlaybackReportUiState.Idle && ReportStatusText.Length == 0) return;
        ReportState = PlaybackReportUiState.Idle;
        ReportStatusText = string.Empty;
        RaiseStateChanged();
    }

    public async Task RequestNextStreamAsync()
    {
        if (!CanGoNext) return;
        if (_requestNext is not null)
        {
            await _requestNext().ConfigureAwait(false);
            return;
        }

        NextStreamRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task RequestPreviousStreamAsync()
    {
        if (_requestPrevious is not null)
        {
            await _requestPrevious().ConfigureAwait(false);
            return;
        }

        PreviousStreamRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Dismiss top chrome layer. Returns false when the host should Exit.
    /// Order: menu → video info → scores.
    /// </summary>
    public bool TryDismissLayer()
    {
        if (IsMenuVisible)
        {
            HideMenu();
            return true;
        }

        if (IsVideoInfoVisible)
        {
            HideVideoInfo();
            return true;
        }

        if (IsScoresVisible)
        {
            IsScoresVisible = false;
            RaiseStateChanged();
            return true;
        }

        return false;
    }

    public void Exit()
    {
        IsMenuVisible = false;
        IsVideoInfoVisible = false;
        IsScoresVisible = false;
        DismissStreamToast();
        ClearReportStatus();
        _cleanupPool?.Invoke();
        RaiseStateChanged();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    public void DismissStreamToast()
    {
        if (!IsStreamToastVisible)
        {
            return;
        }

        IsStreamToastVisible = false;
        StreamToastDismissed?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
