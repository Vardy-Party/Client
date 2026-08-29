using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using AutoFixture;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;
using Xunit;
using VardyParty.Hosting;
using VardyParty.TestSupport;

namespace VardyParty.Hosting.Tests;

public class ServiceCollectionExtensionsTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();

    [Fact]
    public void AddSecrets_AssemblyWithoutUserSecretsId_DoesNotThrowAndConfigurationStillBuilds()
    {
        // Arrange
        var key = _fixture.Create<string>();
        var value = _fixture.Create<string>();
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });

        // Act
        var configuration = builder.AddSecrets(typeof(ServiceCollectionExtensionsTests).Assembly).Build();

        // Assert
        Assert.Equal(value, configuration[key]);
    }

    [Fact]
    public void AddSecrets_UserSecretsIdWithNoSecretsFileOnDisk_DoesNotThrowAndConfigurationStillBuilds()
    {
        // Arrange
        var key = _fixture.Create<string>();
        var value = _fixture.Create<string>();
        var secretsAssembly = CreateAssemblyWithUserSecretsId($"vardyparty-tests-{Guid.NewGuid():N}");
        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.WriteAllText(appSettingsPath, "{ \"AllowUserSecrets\": true }");
        try
        {
            var builder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });

            // Act
            var configuration = builder.AddSecrets(secretsAssembly).Build();

            // Assert
            Assert.Equal(value, configuration[key]);
        }
        finally
        {
            File.Delete(appSettingsPath);
        }
    }

    private static Assembly CreateAssemblyWithUserSecretsId(string userSecretsId)
    {
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"VardyParty.Hosting.Tests.Dynamic.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);

        var attributeCtor = typeof(UserSecretsIdAttribute).GetConstructor([typeof(string)])!;
        assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(attributeCtor, [userSecretsId]));

        return assemblyBuilder;
    }
}
