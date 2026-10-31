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
    /// True when the next SwitchToNextStream call would wrap to the first healthy stream.
    /// The pool order IS the channel numbering (docs/STREAM_PLAYBACK_RULES.md "Stable
    /// channel numbering"): append-only, gaps close on drop, never re-sorted.
    /// </summary>
    bool WouldWrapOnNext();


    /// <summary>
    /// Switches to a healthy stream matching catalog URL + optional chip name.
    /// </summary>
    bool SwitchToMatchingStream(string streamUrl, string? streamName);

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

    // ---- Tried-this-cycle marks (docs/STREAM_PLAYBACK_RULES.md "No loops") ----
    // A row that failed for a stream reason (including "No video (audio only)") is
    // skipped by Next/Previous/auto-advance for a short TTL. Rows keep their numbers;
    // marks never remove anything. Defaults keep other implementations unchanged.

    /// <summary>Marks <paramref name="row"/> as tried-and-failed this cycle; when <paramref name="feedUrl"/> is set its feed (m3u8 identity) is marked failed too.</summary>
    void MarkTriedInCycle(EnrichedStream? row, string? feedUrl, string reason) { }

    /// <summary>Clears the tried mark of <paramref name="row"/> and the failed mark of <paramref name="feedUrl"/>'s feed (it showed video).</summary>
    void ClearTriedInCycle(EnrichedStream? row, string? feedUrl) { }

    /// <summary>True when <paramref name="row"/> is skipped this cycle (marked, or its feed failed).</summary>
    bool IsSkippedInCycle(EnrichedStream? row) => false;

    /// <summary>The reason <paramref name="feedUrl"/>'s feed failed this cycle, or null.</summary>
    string? FailedFeedReason(string? feedUrl) => null;

    /// <summary>True when Next has somewhere untried to go (another unskipped row, or untried fallbacks for the current one).</summary>
    bool HasUntriedOther() => true;
}
