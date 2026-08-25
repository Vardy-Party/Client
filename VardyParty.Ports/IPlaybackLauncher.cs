using VardyParty.Models;

namespace VardyParty.Ports;

/// <summary>
/// Per-call port so stream resolution can start playback without ctor-injecting the player.
/// </summary>
public interface IPlaybackLauncher
{
    event EventHandler<bool>? BufferingStateChanged;

    Task<PlaybackResult> PlayVideoAsync(
        string m3u8Url,
        string refererUrl,
        string title,
        Func<Task>? onNextStreamRequested = null,
        string? league = null,
        string? homeTeam = null,
        string? awayTeam = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null);

    PlaybackMetrics? GetCurrentMetrics();
}
