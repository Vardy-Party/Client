using VardyParty.Hosting;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class AssemblyIdentityTests
{
    [Fact]
    public void HostingTypes_LiveInHostingNamespaceAndAssembly()
    {
        // Arrange
        var validator = typeof(ConfigurationValidator);
        var secrets = typeof(ServiceCollectionExtensions);

        // Act
        var assemblyName = validator.Assembly.GetName().Name;

        // Assert
        Assert.Equal("VardyParty.Hosting", assemblyName);
        Assert.Equal("VardyParty.Hosting", validator.Namespace);
        Assert.Equal("VardyParty.Hosting", secrets.Namespace);
    }
}
