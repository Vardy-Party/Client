using VardyParty.Catalog;
using VardyParty.Presentation;

namespace VardyParty.Desktop.Services;

/// <summary>
/// In-player chrome state for the Linux/desktop head. Same capabilities as
/// the Windows/Android overlays (stream count toast, video info, scores
/// ticker, next/prev, hamburger menu). The page overlays them on the
/// composited LibVLC picture (same feel as Windows/Android).
/// </summary>
public enum DesktopPlayerChromeTimerAction
{
    None,
    StartStreamToastHide,
    CancelStreamToastHide,
}

public enum DesktopPlayerEscapeAction
{
    None,
    DismissMenu,
    DismissInfo,
    ClosePlayback,
}

public sealed class DesktopPlayerChrome
{
    /// <summary>Windows/Android stream toast auto-hide.</summary>
    public static readonly TimeSpan StreamToastDuration = TimeSpan.FromSeconds(10);

    public const double TickerRowHeight = 36;
    public const double InfoColumnWidth = 360;

    private int _lastStreamIndex = -1;
    private int _lastStreamTotal = -1;
    private string? _lastVerticalResolution;

    public bool MenuVisible { get; private set; }

    public bool InfoVisible { get; private set; }

    public bool TickerVisible { get; private set; }

    public ScoresTickerMode TickerMode { get; private set; } = ScoresTickerMode.SameLeagueInPlay;

    public bool StreamToastVisible { get; private set; }

    public string StreamToastText { get; private set; } = string.Empty;

    public string StreamCountHint { get; private set; } = string.Empty;

    public bool CanSwitchStream { get; private set; }

    public int StreamIndex { get; private set; }

    public int StreamTotal { get; private set; }

    public string? SourceBadgeLabel { get; private set; }

    public bool SourceBadgeIsFacebook { get; private set; }

    public bool IsBuffering { get; private set; }

    public string ReportStatus { get; private set; } = string.Empty;

    public bool ReportStatusVisible { get; private set; }

    /// <summary>
    /// Top reserved row must grow for the stream toast, the hamburger menu,
    /// or a match-event toast — never for a "Now Playing" banner.
    /// </summary>
    public bool NeedsExpandedChromeRow =>
        StreamToastVisible || MenuVisible || ReportStatusVisible;

    public DesktopPlayerChromeTimerAction ApplyStreamChrome(
        int index,
        int total,
        string? verticalResolution,
        string? sourceBadgeLabel,
        bool canRequestNext)
    {
        var hasChanged = total != _lastStreamTotal || index != _lastStreamIndex;
        var hasResolutionChanged = !string.Equals(
            _lastVerticalResolution ?? string.Empty,
            verticalResolution ?? string.Empty,
            StringComparison.Ordinal);

        StreamIndex = index;
        StreamTotal = total;
        SourceBadgeLabel = string.IsNullOrWhiteSpace(sourceBadgeLabel) ? null : sourceBadgeLabel;
        SourceBadgeIsFacebook = PlayerChromeText.IsFacebookSource(SourceBadgeLabel);
        CanSwitchStream = canRequestNext && total > 1;
        StreamCountHint = PlayerChromeText.FormatStreamHint(index, total);
        StreamToastText = PlayerChromeText.FormatStreamCount(index, total, verticalResolution);

        _lastStreamIndex = index;
        _lastStreamTotal = total;
        _lastVerticalResolution = verticalResolution;

        if (InfoVisible)
        {
            StreamToastVisible = false;
            return DesktopPlayerChromeTimerAction.CancelStreamToastHide;
        }

        var shouldShow = total > 0 &&
            (hasChanged
             || hasResolutionChanged
             || (!StreamToastVisible && !string.IsNullOrWhiteSpace(verticalResolution)));

        if (!shouldShow)
        {
            return DesktopPlayerChromeTimerAction.None;
        }

        StreamToastVisible = true;
        return DesktopPlayerChromeTimerAction.StartStreamToastHide;
    }

    public void SetBuffering(bool isBuffering) => IsBuffering = isBuffering;

    public void ToggleMenu()
    {
        MenuVisible = !MenuVisible;
        if (MenuVisible)
        {
            InfoVisible = false;
            HideStreamToast();
        }
    }

    public void HideMenu() => MenuVisible = false;

    public void ShowInfo()
    {
        InfoVisible = true;
        MenuVisible = false;
        HideStreamToast();
    }

    public void HideInfo() => InfoVisible = false;

    public void ToggleInfo()
    {
        if (InfoVisible)
        {
            HideInfo();
        }
        else
        {
            ShowInfo();
        }
    }

    public void ToggleTicker()
    {
        TickerVisible = !TickerVisible;
        MenuVisible = false;
        if (TickerVisible)
        {
            TickerMode = ScoresTickerMode.SameLeagueInPlay;
        }
    }

    public void HideTicker() => TickerVisible = false;

    public bool CycleTickerMode()
    {
        if (!TickerVisible)
        {
            return false;
        }

        TickerMode = ScoresTickerPolicy.Next(TickerMode);
        return true;
    }

    public void SetReportStatus(string? status)
    {
        ReportStatus = status ?? string.Empty;
        ReportStatusVisible = !string.IsNullOrWhiteSpace(status);
        if (ReportStatusVisible)
        {
            MenuVisible = true;
        }
    }

    public DesktopPlayerEscapeAction OnEscape()
    {
        if (MenuVisible)
        {
            HideMenu();
            return DesktopPlayerEscapeAction.DismissMenu;
        }

        if (InfoVisible)
        {
            HideInfo();
            return DesktopPlayerEscapeAction.DismissInfo;
        }

        return DesktopPlayerEscapeAction.ClosePlayback;
    }

    public void HideStreamToast() => StreamToastVisible = false;

    public void Reset()
    {
        MenuVisible = false;
        InfoVisible = false;
        TickerVisible = false;
        TickerMode = ScoresTickerMode.SameLeagueInPlay;
        StreamToastVisible = false;
        StreamToastText = string.Empty;
        StreamCountHint = string.Empty;
        CanSwitchStream = false;
        StreamIndex = 0;
        StreamTotal = 0;
        SourceBadgeLabel = null;
        SourceBadgeIsFacebook = false;
        IsBuffering = false;
        ReportStatus = string.Empty;
        ReportStatusVisible = false;
        _lastStreamIndex = -1;
        _lastStreamTotal = -1;
        _lastVerticalResolution = null;
    }
}
