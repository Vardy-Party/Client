using System;
using System.Collections.Generic;
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
        // Arrange
        var isTelevisionDevice = isTv;

        // Act
        var enabled = LauncherComponentRules.DesiredPhoneLauncherEnabled(isTelevisionDevice);

        // Assert
        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesiredPhoneLauncherEnabled_MatchesPhoneSplashHandoff(bool isTv)
    {
        // Arrange
        var handoff = PhoneSplashHandoff.ShouldAdvertisePhoneLauncher(isTv);

        // Act
        var enabled = LauncherComponentRules.DesiredPhoneLauncherEnabled(isTv);

        // Assert
        Assert.Equal(handoff, enabled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DesiredTvLeanbackLauncherEnabled_TelevisionOnly(bool isTv, bool expected)
    {
        // Arrange
        var isTelevisionDevice = isTv;

        // Act
        var enabled = LauncherComponentRules.DesiredTvLeanbackLauncherEnabled(isTelevisionDevice);

        // Assert
        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesiredTvLeanbackLauncherEnabled_MatchesPhoneSplashHandoff(bool isTv)
    {
        // Arrange
        var handoff = PhoneSplashHandoff.ShouldAdvertiseTvLeanbackLauncher(isTv);

        // Act
        var enabled = LauncherComponentRules.DesiredTvLeanbackLauncherEnabled(isTv);

        // Assert
        Assert.Equal(handoff, enabled);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CanDisablePhoneLauncher_OnlyWhenNoTaskIsRootedAtSplash(bool anyAppTaskRootedAtSplash, bool expected)
    {
        // Arrange
        var rootedAtSplash = anyAppTaskRootedAtSplash;

        // Act
        var canDisable = LauncherComponentRules.CanDisablePhoneLauncher(rootedAtSplash);

        // Assert
        Assert.Equal(expected, canDisable);
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
        // Arrange
        var componentEnabledState = state;

        // Act
        var enabled = LauncherComponentRules.IsEffectivelyEnabled(componentEnabledState, manifestDefault);

        // Assert
        Assert.Equal(expected, enabled);
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_TvWithTaskRootedAtSplash_DoesNotDisable()
    {
        // Arrange - the 3.6.1-b262 bounce: Splash enabled (manifest default) and
        // the first TV session's task is rooted at SplashActivity.
        const bool anyAppTaskRootedAtSplash = true;

        // Act
        var apply = LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: true,
            anyAppTaskRootedAtSplash,
            currentlyEnabled: true);

        // Assert
        Assert.False(apply);
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_TvWithNoTaskRootedAtSplash_DisablesEnabledSplash()
    {
        // Arrange
        const bool anyAppTaskRootedAtSplash = false;

        // Act
        var apply = LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: true,
            anyAppTaskRootedAtSplash,
            currentlyEnabled: true);

        // Assert
        Assert.True(apply);
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_TvWithNoTaskRootedAtSplash_NoOpWhenAlreadyDisabled()
    {
        // Arrange
        const bool currentlyEnabled = false;

        // Act
        var apply = LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: true,
            anyAppTaskRootedAtSplash: false,
            currentlyEnabled);

        // Assert
        Assert.False(apply);
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_Phone_EnablesDisabledSplash()
    {
        // Arrange
        const bool currentlyEnabled = false;

        // Act
        var apply = LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: false,
            anyAppTaskRootedAtSplash: true,
            currentlyEnabled);

        // Assert
        Assert.True(apply);
    }

    [Fact]
    public void ShouldApplyPhoneLauncherState_Phone_NoOpWhenAlreadyEnabled()
    {
        // Arrange
        const bool currentlyEnabled = true;

        // Act
        var apply = LauncherComponentRules.ShouldApplyPhoneLauncherState(
            isTelevisionDevice: false,
            anyAppTaskRootedAtSplash: true,
            currentlyEnabled);

        // Assert
        Assert.False(apply);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void ShouldApplyTvLeanbackLauncherState_OnlyWhenDifferent(
        bool isTv, bool currentlyEnabled, bool expected)
    {
        // Arrange
        var isTelevisionDevice = isTv;

        // Act
        var apply = LauncherComponentRules.ShouldApplyTvLeanbackLauncherState(isTelevisionDevice, currentlyEnabled);

        // Assert
        Assert.Equal(expected, apply);
    }

    [Theory]
    [InlineData(LauncherComponentRules.TvLeanbackAliasClassName)]
    [InlineData("com.vardyparty.MainActivity")]
    public void AnyAppTaskRootedAtSplash_OnlyNonSplashRoots_IsFalse(string root)
    {
        // Arrange - e.g. a force-stop, then the Leanback tile: the new task's
        // root intent is the alias.
        var roots = new[] { root };

        // Act
        var rootedAtSplash = LauncherComponentRules.AnyAppTaskRootedAtSplash(roots);

        // Assert
        Assert.False(rootedAtSplash);
    }

    [Fact]
    public void AnyAppTaskRootedAtSplash_SplashRootedTaskBesideAliasTask_IsTrue()
    {
        // Arrange - MainActivity is on top of an alias task, but an older task in
        // recents is still rooted at Splash; disabling would remove it.
        var roots = new[] { LauncherComponentRules.TvLeanbackAliasClassName, LauncherComponentRules.PhoneLauncherClassName };

        // Act
        var rootedAtSplash = LauncherComponentRules.AnyAppTaskRootedAtSplash(roots);

        // Assert
        Assert.True(rootedAtSplash);
    }

    [Fact]
    public void AnyAppTaskRootedAtSplash_SplashHandoffTask_IsTrue()
    {
        // Arrange - Splash started MainActivity in its own task, so MainActivity's
        // intent says MainActivity but the task root is still Splash.
        var roots = new[] { LauncherComponentRules.PhoneLauncherClassName };

        // Act
        var rootedAtSplash = LauncherComponentRules.AnyAppTaskRootedAtSplash(roots);

        // Assert
        Assert.True(rootedAtSplash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnyAppTaskRootedAtSplash_UnknownTaskRoot_IsConservative(string? root)
    {
        // Arrange
        var roots = new[] { LauncherComponentRules.TvLeanbackAliasClassName, root };

        // Act
        var rootedAtSplash = LauncherComponentRules.AnyAppTaskRootedAtSplash(roots);

        // Assert
        Assert.True(rootedAtSplash);
    }

    [Fact]
    public void AnyAppTaskRootedAtSplash_TasksUnreadable_IsConservative()
    {
        // Arrange
        IReadOnlyCollection<string?>? roots = null;

        // Act
        var rootedAtSplash = LauncherComponentRules.AnyAppTaskRootedAtSplash(roots);

        // Assert
        Assert.True(rootedAtSplash);
    }

    [Fact]
    public void AnyAppTaskRootedAtSplash_NoTasks_IsConservative()
    {
        // Arrange - the caller's own task should always be listed.
        var roots = Array.Empty<string?>();

        // Act
        var rootedAtSplash = LauncherComponentRules.AnyAppTaskRootedAtSplash(roots);

        // Assert
        Assert.True(rootedAtSplash);
    }
}