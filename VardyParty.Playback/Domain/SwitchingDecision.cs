using VardyParty.Playback;

namespace VardyParty.Playback
{
    /// <summary>
    /// Legacy switch-eligibility helper. Prefer <see cref="PlaybackPolicy.CanAttach"/>.
    /// </summary>
    public static class SwitchingDecision
    {
        public static bool CanSwitch(string? currentUrl, string candidateUrl, bool isPreparing)
            => PlaybackPolicy.CanAttach(currentUrl, candidateUrl, isPreparing);
    }
}
