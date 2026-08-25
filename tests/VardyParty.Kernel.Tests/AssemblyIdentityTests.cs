using VardyParty.Kernel;
using Xunit;

namespace VardyParty.Kernel.Tests;

public class AssemblyIdentityTests
{
    [Fact]
    public void KernelTypes_LiveInKernelNamespaceAndAssembly()
    {
        // Arrange
        var game = typeof(Game);
        var confidence = typeof(RecommendationConfidence);
        var settings = typeof(APISettings);
        var down = typeof(ApiSystemDownException);

        // Act
        var assemblyName = game.Assembly.GetName().Name;

        // Assert
        Assert.Equal("VardyParty.Kernel", assemblyName);
        Assert.Equal("VardyParty.Kernel", game.Namespace);
        Assert.Equal("VardyParty.Kernel", confidence.Namespace);
        Assert.Equal("VardyParty.Kernel", settings.Namespace);
        Assert.Equal("VardyParty.Kernel", down.Namespace);
    }
}
