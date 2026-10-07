namespace VardyParty.Presentation;

/// <summary>
/// The phone screen stays on while finding streams or playing.
/// A locked phone dropped the paired-computer tunnel mid-search.
/// </summary>
public static class ScreenAwakePolicy
{
    public static bool Hold(bool findingStreams, bool playing) =>
        findingStreams || playing;

    /// <summary>
    /// The keep-screen-on window flag is a view change. Playback visibility is
    /// raised from a pool thread, and a flag change from that thread aborted
    /// the app on the phone, so the flag always goes through the UI dispatcher.
    /// </summary>
    public static void Apply(bool on, Action<Action> dispatchToUi, Action<bool> setWindowFlag)
    {
        ArgumentNullException.ThrowIfNull(dispatchToUi);
        ArgumentNullException.ThrowIfNull(setWindowFlag);
        dispatchToUi(() => setWindowFlag(on));
    }
}
