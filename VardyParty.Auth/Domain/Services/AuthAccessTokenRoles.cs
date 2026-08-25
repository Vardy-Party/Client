using System.Text.Json;

namespace VardyParty.Auth;

/// <summary>
/// JWT access-token role gate shared by MAUI and Linux Auth0 hosts.
/// </summary>
public static class AuthAccessTokenRoles
{
    public static bool HasRequiredRole(string accessToken, string? claimType, string? requiredRole)
    {
        if (string.IsNullOrWhiteSpace(claimType) || string.IsNullOrWhiteSpace(requiredRole))
            return true;

        if (string.IsNullOrWhiteSpace(accessToken))
            return false;

        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2)
                return false;

            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            if (!doc.RootElement.TryGetProperty(claimType, out var claim))
                return false;

            if (claim.ValueKind == JsonValueKind.String)
            {
                var raw = claim.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    return false;

                return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(value => string.Equals(value, requiredRole, StringComparison.OrdinalIgnoreCase));
            }

            if (claim.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in claim.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String &&
                        string.Equals(item.GetString(), requiredRole, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    internal static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }
}
