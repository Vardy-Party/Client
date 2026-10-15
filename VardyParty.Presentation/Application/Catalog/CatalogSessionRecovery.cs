namespace VardyParty.Presentation;

/// <summary>
/// What the homepage does after a catalog 401 or a resume auth recheck.
/// Android TV (<c>HomeHostPage</c>) and Linux (<c>LinuxHomePage</c>) paint
/// the overlay; this type owns the policy so the two hosts cannot drift.
/// </summary>
/// <remarks>
/// <code>
/// probe result          catalog 401                         resume
/// -----------------     --------------------------------    -----------------
/// still signed in       keep board, recovery banner        stay on the board
/// signed out            open sign-in                        open sign-in
/// check threw           open sign-in (not "still signed")   open sign-in
/// already signed out
/// or sign-in running    do nothing                          do nothing
/// </code>
/// A thrown token read is a dead session. It is not evidence that a refresh
/// token is still present.
/// </remarks>
public static class CatalogSessionRecovery
{
    /// <summary>
    /// Result of <c>IAuthTokenProvider.IsAuthenticatedAsync</c>, including
    /// the case where the check itself threw (keystore or storage failure).
    /// </summary>
    public enum Probe
    {
        StillSignedIn,
        SignedOut,
        CheckFailed,
    }

    /// <param name="OpenSignIn">Replace the board with the sign-in overlay.</param>
    /// <param name="ShowRecoveryBanner">
    /// Leave the last board up and show <see cref="RefreshFailureBanner"/>.
    /// </param>
    public readonly record struct Decision(bool OpenSignIn, bool ShowRecoveryBanner);

    public const string RefreshFailureBanner =
        "Couldn't refresh sign-in. Games will update when the session recovers.";

    public static Decision AfterCatalogUnauthorized(
        bool showingSignedInHome, bool signInInProgress, Probe probe) =>
        Decide(showingSignedInHome, signInInProgress, probe, bannerWhenStillSignedIn: true);

    public static Decision AfterResume(
        bool showingSignedInHome, bool signInInProgress, Probe probe) =>
        Decide(showingSignedInHome, signInInProgress, probe, bannerWhenStillSignedIn: false);

    private static Decision Decide(
        bool showingSignedInHome, bool signInInProgress, Probe probe, bool bannerWhenStillSignedIn)
    {
        if (!showingSignedInHome || signInInProgress)
        {
            return default;
        }

        if (probe == Probe.StillSignedIn)
        {
            return new Decision(OpenSignIn: false, ShowRecoveryBanner: bannerWhenStillSignedIn);
        }

        return new Decision(OpenSignIn: true, ShowRecoveryBanner: false);
    }
}
