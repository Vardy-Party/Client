using VardyParty.Streaming;
using Xunit;

namespace VardyParty.Streaming.Tests;

public class AssemblyIdentityTests
{
    [Fact]
    public void ClientApiVersion_LivesInStreamingNamespaceAndAssembly()
    {
        // Arrange
        var type = typeof(VardyPartyClientApiVersion);

        // Act
        var assemblyName = type.Assembly.GetName().Name;

        // Assert
        Assert.Equal("VardyParty.Streaming", assemblyName);
        Assert.Equal("VardyParty.Streaming", type.Namespace);
    }
}
