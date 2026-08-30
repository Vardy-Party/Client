using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class PhoneSplashHandoffTests
{
    [Fact]
    public void ShouldBuildMaui_FirstDraw_StartsHost()
    {
        // Arrange / Act
        var should = PhoneSplashHandoff.ShouldBuildMaui(
            alreadyHandedOff: false, isFinishing: false, isDestroyed: false);

        // Assert
        Assert.True(should);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ShouldBuildMaui_SkipsWhenAlreadyGone(bool handedOff, bool finishing, bool destroyed)
    {
        // Arrange / Act
        var should = PhoneSplashHandoff.ShouldBuildMaui(handedOff, finishing, destroyed);

        // Assert
        Assert.False(should);
    }

    [Fact]
    public void ShouldStartMainActivity_AfterMaui_WhenStillAlive()
    {
        // Arrange / Act
        var should = PhoneSplashHandoff.ShouldStartMainActivity(
            mauiStarted: true, isFinishing: false, isDestroyed: false);

        // Assert
        Assert.True(should);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void ShouldStartMainActivity_DoesNotLaunchDeadActivity(
        bool mauiStarted, bool finishing, bool destroyed)
    {
        // Arrange / Act
        var should = PhoneSplashHandoff.ShouldStartMainActivity(mauiStarted, finishing, destroyed);

        // Assert
        Assert.False(should);
    }

    [Fact]
    public void ShouldAdvertisePhoneLauncher_PhonesOnly()
    {
        // Arrange / Act / Assert
        Assert.True(PhoneSplashHandoff.ShouldAdvertisePhoneLauncher(isTelevisionDevice: false));
        Assert.False(PhoneSplashHandoff.ShouldAdvertisePhoneLauncher(isTelevisionDevice: true));
    }

    [Fact]
    public void ShouldAdvertiseTvLeanbackLauncher_TelevisionOnly()
    {
        // Arrange / Act / Assert
        Assert.False(PhoneSplashHandoff.ShouldAdvertiseTvLeanbackLauncher(isTelevisionDevice: false));
        Assert.True(PhoneSplashHandoff.ShouldAdvertiseTvLeanbackLauncher(isTelevisionDevice: true));
    }

    [Fact]
    public void ShouldBuildMauiOnLooperIdle_WaitsForFrameAndIdle()
    {
        // Arrange / Act / Assert
        Assert.False(PhoneSplashHandoff.ShouldBuildMauiOnLooperIdle(
            splashFrameSubmitted: false, looperIdle: true));
        Assert.False(PhoneSplashHandoff.ShouldBuildMauiOnLooperIdle(
            splashFrameSubmitted: true, looperIdle: false));
        Assert.True(PhoneSplashHandoff.ShouldBuildMauiOnLooperIdle(
            splashFrameSubmitted: true, looperIdle: true));
    }
}
