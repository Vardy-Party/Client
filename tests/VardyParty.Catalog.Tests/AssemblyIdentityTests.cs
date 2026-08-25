using VardyParty.Catalog;
using Xunit;

namespace VardyParty.Catalog.Tests;

public class AssemblyIdentityTests
{
    [Fact]
    public void DisplayExtensions_LivesInCatalogNamespaceAndAssembly()
    {
        // Arrange
        var type = typeof(DisplayExtensions);

        // Act
        var assemblyName = type.Assembly.GetName().Name;

        // Assert
        Assert.Equal("VardyParty.Catalog", assemblyName);
        Assert.Equal("VardyParty.Catalog", type.Namespace);
    }
}
