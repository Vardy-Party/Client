namespace VardyParty.Presentation;

/// <summary>
/// Result of queuing a downloaded package with the OS/snapd helper.
/// </summary>
public readonly record struct DesktopApplyResult(bool CallerShouldQuit);
