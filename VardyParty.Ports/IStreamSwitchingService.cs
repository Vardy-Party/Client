using VardyParty.Kernel;

namespace VardyParty.Ports;

/// <summary>
/// Service that manages stream switching during video playback.
/// Provides a way to notify the video player about new healthy streams discovered during testing.
/// </summary>
public interface IStreamSwitchingService
{
    /// <summary>
    /// Observable stream of available healthy streams
    /// </summary>
    IObservable<IReadOnlyList<EnrichedStream>> HealthyStreamsUpdated { get; }

    /// <summary>
    /// Observable for current playback stream index
    /// </summary>
    IObservable<int> CurrentStreamIndexChanged { get; }

    /// <summary>
    /// Observable that emits the currently selected EnrichedStream (or null) when it changes
    /// </summary>
    IObservable<EnrichedStream?> CurrentStreamChanged { get; }

    /// <summary>
    /// Observable that emits richer overlay info for native players (index/total/channel/bitrate)
    /// </summary>
    IObservable<PlayerOverlayInfo?> OverlayInfoChanged { get; }

    /// <summary>
    /// Initializes the service for a new playback session
    /// </summary>
    void Initialize(string league, string homeTeam, string awayTeam);

    /// <summary>
    /// Adds a newly discovered healthy stream
    /// </summary>
    void AddHealthyStream(EnrichedStream stream);

    /// <summary>
    /// Switches to the next available stream
    /// </summary>
    bool SwitchToNextStream();

    /// <summary>
    /// Switches to the previous available stream
    /// </summary>
    bool SwitchToPreviousStream();

    /// <summary>
    /// Switches to a specific stream by index
    /// </summary>
    bool SwitchToStream(int index);

    /// <summary>
    /// Gets the current stream being played
    /// </summary>
    EnrichedStream? GetCurrentStream();

    /// <summary>
    /// Gets the next healthy stream without switching to it
    /// </summary>
    EnrichedStream? GetNextHealthyStream();

    /// <summary>
    /// Gets all healthy streams discovered so far
    /// </summary>
    IReadOnlyList<EnrichedStream> GetHealthyStreams();

    /// <summary>
    /// Gets the current stream index (1-based for display)
    /// </summary>
    int GetCurrentStreamIndex();

    /// <summary>
    /// Completes the playback session and cleans up
    /// </summary>
    void Cleanup();

    /// <summary>
    /// Removes the current stream from the healthy list (used when playback fails)
    /// </summary>
    bool RemoveCurrentStream();
}
