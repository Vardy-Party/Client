using System;
using System.Text;
using AutoFixture;
using Xunit;
using VardyParty.Auth;
using VardyParty.TestSupport;

namespace VardyParty.Auth.Tests;

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

    [Fact]
    public void HasRelayUser_WhenRolesClaimHasRelayUser_ReturnsTrue()
    {
        var token = CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":["{{OakLaneMember}}","relay-user"]}""");

        Assert.True(AuthAccessTokenRoles.HasRelayUser(token, NorthgateRoleClaim));
    }

    [Fact]
    public void HasRelayUser_WhenPermissionsClaimHasRelayUser_ReturnsTrue()
    {
        var token = CreateUnsignedJwt("""{"permissions":["relay-user"]}""");

        Assert.True(AuthAccessTokenRoles.HasRelayUser(token, NorthgateRoleClaim));
    }

    [Fact]
    public void HasRelayUser_WhenSiblingPermissionsClaimHasRelayUser_ReturnsTrue()
    {
        var token = CreateUnsignedJwt("""{"https://northgate.test/permissions":["relay-user"]}""");

        Assert.True(AuthAccessTokenRoles.HasRelayUser(token, NorthgateRoleClaim));
    }

    [Fact]
    public void HasRelayUser_WhenTokenLacksRelayUser_ReturnsFalse()
    {
        var token = CreateUnsignedJwt($$"""{"{{NorthgateRoleClaim}}":["{{OakLaneMember}}"]}""");

        Assert.False(AuthAccessTokenRoles.HasRelayUser(token, NorthgateRoleClaim));
    }

    [Fact]
    public void HasRelayUser_WhenTokenMissing_ReturnsFalse()
    {
        Assert.False(AuthAccessTokenRoles.HasRelayUser(accessToken: null, NorthgateRoleClaim));
        Assert.False(AuthAccessTokenRoles.HasRelayUser(accessToken: " ", NorthgateRoleClaim));
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
