using System.Text.Json.Serialization;

namespace VardyParty.Kernel;

/// <summary>
/// The reasons a playback session reports when the viewer leaves the player. One place, so the
/// producers (session controller, Android activity) and the consumers (resolution orchestrator)
/// cannot drift apart on a magic string.
/// </summary>
public static class PlaybackCloseReasons
{
    /// <summary>The viewer pressed Back / Stop and the session controller closed the session.</summary>
    public const string UserClosed = "User closed";

    /// <summary>
    /// The Android player activity was destroyed without going through the session controller
    /// (system teardown, Home, task removal, the OnBackPressed fallback).
    /// </summary>
    public const string PlayerClosed = "Video player closed";
}

public class PlaybackResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsRecoverable { get; set; }

    /// <summary>The viewer closed the player (Back / Stop). Also true for the Windows and Linux "User closed ..." messages.</summary>
    [JsonIgnore]
    public bool IsUserClosed => (Message ?? string.Empty).Contains(PlaybackCloseReasons.UserClosed, StringComparison.OrdinalIgnoreCase);

    /// <summary>The player window went away without a user close (see <see cref="PlaybackCloseReasons.PlayerClosed"/>).</summary>
    [JsonIgnore]
    public bool IsPlayerClosed => (Message ?? string.Empty).Contains(PlaybackCloseReasons.PlayerClosed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Nobody is watching any more: the user closed the player, or its window was destroyed.
    /// Candidate testing must stop, there is nothing to fail over to, and the player must not be
    /// relaunched.
    /// </summary>
    [JsonIgnore]
    public bool ViewerLeft => IsUserClosed || IsPlayerClosed;

    public static PlaybackResult Completed(string message, bool recoverable = false) => new()
    {
        Success = false,
        Message = message,
        IsRecoverable = recoverable
    };

    public static PlaybackResult SuccessResult(string message) => new()
    {
        Success = true,
        Message = message,
        IsRecoverable = true
    };
}