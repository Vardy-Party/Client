using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

/// <summary>
/// The TV idle invariant: an idle homepage on the TV class schedules ZERO
/// periodic animation ticks. Both permanent tick sources (per-card live-dot
/// pulses, crest ambient shimmer) must be denied on TV and allowed elsewhere.
/// </summary>
public class HomeIdleAnimationPolicyTests
{
    [Fact]
    public void AllowLiveDotPulse_Tv_IsDenied()
    {
        // Arrange
        var layoutClass = HomeLayoutClass.Tv;

        // Act
        var allowed = HomeIdleAnimationPolicy.AllowLiveDotPulse(layoutClass);

        // Assert
        Assert.False(allowed);
    }

    [Theory]
    [InlineData(HomeLayoutClass.Desktop)]
    [InlineData(HomeLayoutClass.PhoneLandscape)]
    [InlineData(HomeLayoutClass.PhonePortrait)]
    public void AllowLiveDotPulse_NonTvClasses_KeepThePulse(HomeLayoutClass layoutClass)
    {
        // Arrange: GPU-backed classes keep the ambient identity.

        // Act
        var allowed = HomeIdleAnimationPolicy.AllowLiveDotPulse(layoutClass);

        // Assert
        Assert.True(allowed);
    }

    [Fact]
    public void AllowAmbientCrestShimmer_Tv_IsDenied()
    {
        // Arrange
        var layoutClass = HomeLayoutClass.Tv;

        // Act
        var allowed = HomeIdleAnimationPolicy.AllowAmbientCrestShimmer(layoutClass);

        // Assert
        Assert.False(allowed);
    }

    [Theory]
    [InlineData(HomeLayoutClass.Desktop)]
    [InlineData(HomeLayoutClass.PhoneLandscape)]
    [InlineData(HomeLayoutClass.PhonePortrait)]
    public void AllowAmbientCrestShimmer_NonTvClasses_KeepTheShimmer(HomeLayoutClass layoutClass)
    {
        // Arrange: GPU-backed classes keep the ambient identity.

        // Act
        var allowed = HomeIdleAnimationPolicy.AllowAmbientCrestShimmer(layoutClass);

        // Assert
        Assert.True(allowed);
    }
}
