namespace VardyParty.Presentation;

/// <summary>
/// Phone launcher paints splash, then starts MAUI. Do not hand off after
/// the activity is finishing or destroyed (Back during the wait, process
/// death). Android types stay out of this type so the rules are unit tested.
/// </summary>
public static class PhoneSplashHandoff
{
    public static bool ShouldBuildMaui(bool alreadyHandedOff, bool isFinishing, bool isDestroyed) =>
        !alreadyHandedOff && !isFinishing && !isDestroyed;

    public static bool ShouldStartMainActivity(bool mauiStarted, bool isFinishing, bool isDestroyed) =>
        mauiStarted && !isFinishing && !isDestroyed;
}
