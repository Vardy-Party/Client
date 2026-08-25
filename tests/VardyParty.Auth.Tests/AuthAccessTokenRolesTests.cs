using System;
using System.Text;
using AutoFixture;
using Xunit;
using VardyParty.Auth;

namespace VardyParty.Tests;

public class AuthAccessTokenRolesTests
{
    private readonly IFixture _fixture = AutoMoqFixture.Create();
    private const string NorthgateRoleClaim = "https://northgate.test/roles";
    private const string OakLaneMember = "oak-lane-member";

    [Fact]
    public void HasRequiredRole_WhenClaimTypeOrRoleMissing_ReturnsTrue()
    {
        // Arrange
        var token = _fixture.Create<string>();

        // Act
        var noClaimType = AuthAccessTokenRoles.HasRequiredRole(token, claimType: null, OakLaneMember);
        var noRole = AuthAccessTokenRoles.HasRequiredRole(token, NorthgateRoleClaim, requiredRole: " ");

        // Assert
        Assert.True(noClaimType);
        Assert.True(noRole);
    }

    [Fact]
    public void HasRequiredRole_WhenAccessTokenHasSpaceDelimitedRole_ReturnsTrue()
    {
        // Arrange
        var token = CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":"spectator {{OakLaneMember}}"}""");

        // Act
        var accepted = AuthAccessTokenRoles.HasRequiredRole(token, NorthgateRoleClaim, OakLaneMember);

        // Assert
        Assert.True(accepted);
    }

    [Fact]
    public void HasRequiredRole_WhenAccessTokenHasRoleArray_ReturnsTrue()
    {
        // Arrange
        var token = CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":["{{OakLaneMember}}","scoreboard"]}""");

        // Act
        var accepted = AuthAccessTokenRoles.HasRequiredRole(token, NorthgateRoleClaim, OakLaneMember);

        // Assert
        Assert.True(accepted);
    }

    [Fact]
    public void HasRequiredRole_WhenAccessTokenLacksRole_ReturnsFalse()
    {
        // Arrange
        var token = CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":"scoreboard"}""");

        // Act
        var accepted = AuthAccessTokenRoles.HasRequiredRole(token, NorthgateRoleClaim, OakLaneMember);

        // Assert
        Assert.False(accepted);
    }

    internal static string CreateUnsignedJwt(string payloadJson)
    {
        var header = Base64UrlEncode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncode(payloadJson);
        return $"{header}.{payload}.oak-sig";
    }

    private static string Base64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
