using Xunit;
using VardyParty.Presentation;

namespace VardyParty.Presentation.Tests;

public class CatalogSessionRecoveryTests
{
    [Fact]
    public void AfterCatalogUnauthorized_StillSignedIn_KeepsBoardAndShowsBanner()
    {
        // Arrange
        var probe = CatalogSessionRecovery.Probe.StillSignedIn;

        // Act
        var decision = CatalogSessionRecovery.AfterCatalogUnauthorized(
            showingSignedInHome: true, signInInProgress: false, probe);

        // Assert
        Assert.False(decision.OpenSignIn);
        Assert.True(decision.ShowRecoveryBanner);
    }

    [Fact]
    public void AfterCatalogUnauthorized_SignedOut_OpensSignIn()
    {
        // Arrange
        var probe = CatalogSessionRecovery.Probe.SignedOut;

        // Act
        var decision = CatalogSessionRecovery.AfterCatalogUnauthorized(
            showingSignedInHome: true, signInInProgress: false, probe);

        // Assert
        Assert.True(decision.OpenSignIn);
        Assert.False(decision.ShowRecoveryBanner);
    }

    [Fact]
    public void AfterCatalogUnauthorized_CheckFailed_OpensSignIn()
    {
        // Arrange
        var probe = CatalogSessionRecovery.Probe.CheckFailed;

        // Act
        var decision = CatalogSessionRecovery.AfterCatalogUnauthorized(
            showingSignedInHome: true, signInInProgress: false, probe);

        // Assert
        Assert.True(decision.OpenSignIn);
        Assert.False(decision.ShowRecoveryBanner);
    }

    [Fact]
    public void AfterCatalogUnauthorized_SignInAlreadyInProgress_DoesNothing()
    {
        // Arrange
        var probe = CatalogSessionRecovery.Probe.SignedOut;

        // Act
        var decision = CatalogSessionRecovery.AfterCatalogUnauthorized(
            showingSignedInHome: true, signInInProgress: true, probe);

        // Assert
        Assert.False(decision.OpenSignIn);
        Assert.False(decision.ShowRecoveryBanner);
    }

    [Fact]
    public void AfterResume_SignedOut_OpensSignIn()
    {
        // Arrange
        var probe = CatalogSessionRecovery.Probe.SignedOut;

        // Act
        var decision = CatalogSessionRecovery.AfterResume(
            showingSignedInHome: true, signInInProgress: false, probe);

        // Assert
        Assert.True(decision.OpenSignIn);
        Assert.False(decision.ShowRecoveryBanner);
    }

    [Fact]
    public void AfterResume_StillSignedIn_DoesNothing()
    {
        // Arrange
        var probe = CatalogSessionRecovery.Probe.StillSignedIn;

        // Act
        var decision = CatalogSessionRecovery.AfterResume(
            showingSignedInHome: true, signInInProgress: false, probe);

        // Assert
        Assert.False(decision.OpenSignIn);
        Assert.False(decision.ShowRecoveryBanner);
    }

    [Fact]
    public void AfterResume_CheckFailed_OpensSignIn()
    {
        // Arrange
        var probe = CatalogSessionRecovery.Probe.CheckFailed;

        // Act
        var decision = CatalogSessionRecovery.AfterResume(
            showingSignedInHome: true, signInInProgress: false, probe);

        // Assert
        Assert.True(decision.OpenSignIn);
        Assert.False(decision.ShowRecoveryBanner);
    }
}
