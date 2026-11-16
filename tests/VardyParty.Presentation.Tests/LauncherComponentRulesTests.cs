using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class LauncherComponentRulesTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DesiredPhoneLauncherEnabled_PhonesOnly(bool isTv, bool expected)
    {
        Assert.Equal(expected, LauncherComponentRules.DesiredPhoneLauncherEnabled(isTv));
        Assert.Equal(
            PhoneSplashHandoff.ShouldAdvertisePhoneLauncher(isTv),
            LauncherComponentRules.DesiredPhoneLauncherEnabled(isTv));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DesiredTvLeanbackLauncherEnabled_TelevisionOnly(bool isTv, bool expected)
    {
        Assert.Equal(expected, LauncherComponentRules.DesiredTvLeanbackLauncherEnabled(isTv));
        Assert.Equal(
            PhoneSplashHandoff.ShouldAdvertiseTvLeanbackLauncher(isTv),
            LauncherComponentRules.DesiredTvLeanbackLauncherEnabled(isTv));
    }

    [Fact]
    public void CanDisablePhoneLauncher_OnlyWhenLaunchDidNotEnterViaSplash()
    {
        Assert.False(LauncherComponentRules.CanDisablePhoneLauncher(launchEnteredViaSplash: true));
        Assert.True(LauncherComponentRules.CanDisablePhoneLauncher(launchEnteredViaSplash: false));
    }

    [Theory]
    [InlineData(LauncherComponentRules.ComponentStateEnabled, false, true)]
    [InlineData(LauncherComponentRules.ComponentStateDisabled, true, false)]
    [InlineData(LauncherComponentRules.ComponentStateDisabledUser, true, false)]
    [InlineData(LauncherComponentRules.ComponentStateDisabledUntilUsed, true, false)]
    [InlineData(LauncherComponentRules.ComponentStateDefault, true, true)]
    [InlineData(LauncherComponentRules.ComponentStateDefault, false, false)]
    [InlineData(99, true, true)]
    [InlineData(99, false, false)]
    public void IsEffectivelyEnabled_UsesExplicitStateOrManifestDefault(
        int state, bool manifestDefault, bool expected)
    {
        Assert.Equal(
            expected,
            LauncherComponentRules.IsEffectivelyEnabled(state, manifestDefault));
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_TvFirstLaunchViaSplash_DoesNotDisable()
    {
        // The 3.6.1-b262 bounce: Splash was already enabled (manifest default)
        // and the first TV session entered through it.
        Assert.False(LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: true,
            launchEnteredViaSplash: true,
            currentlyEnabled: true));
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_TvLaunchViaLeanback_DisablesEnabledSplash()
    {
        Assert.True(LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: true,
            launchEnteredViaSplash: false,
            currentlyEnabled: true));
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_TvLaunchViaLeanback_NoOpWhenAlreadyDisabled()
    {
        Assert.False(LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: true,
            launchEnteredViaSplash: false,
            currentlyEnabled: false));
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_Phone_EnablesDisabledSplash()
    {
        Assert.True(LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: false,
            launchEnteredViaSplash: true,
            currentlyEnabled: false));
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_Phone_NoOpWhenAlreadyEnabled()
    {
        Assert.False(LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: false,
            launchEnteredViaSplash: true,
            currentlyEnabled: true));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void ShouldApplyTvLeanbackLauncherState_OnlyWhenDifferent(
        bool isTv, bool currentlyEnabled, bool expected)
    {
        Assert.Equal(
            expected,
            LauncherComponentRules.ShouldApplyTvLeanbackLauncherState(isTv, currentlyEnabled));
    }

    [Fact]
    public void LaunchEnteredViaSplash_TreatsAliasAsSafeToDisable()
    {
        Assert.False(LauncherComponentRules.LaunchEnteredViaSplash(
            LauncherComponentRules.TvLeanbackAliasClassName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("com.vardyparty.SplashActivity")]
    [InlineData("com.vardyparty.MainActivity")]
    public void LaunchEnteredViaSplash_ConservativeForUnknownOrSplashHandoff(string? component)
    {
        Assert.True(LauncherComponentRules.LaunchEnteredViaSplash(component));
    }
}
