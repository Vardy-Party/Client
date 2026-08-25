using VardyParty.Auth;
using Xunit;

namespace VardyParty.Auth.Tests;

public class AssemblyIdentityTests
{
    [Fact]
    public void Auth0Settings_LivesInAuthNamespaceAndAssembly()
    {
        // Arrange
        var type = typeof(Auth0Settings);

        // Act
        var assemblyName = type.Assembly.GetName().Name;

        // Assert
        Assert.Equal("VardyParty.Auth", assemblyName);
        Assert.Equal("VardyParty.Auth", type.Namespace);
    }
}
