using System.Security.Claims;
using System.Text.Json;

namespace Rbac.Api;

/// <summary>
/// All of this API's roles live on the <c>rbac-api</c> client in Keycloak, so the token carries
/// one flat list:
/// <code>
///   "resource_access": { "rbac-api": { "roles": ["admin", "order:read", "order:write", ...] } }
/// </code>
/// Names containing <c>:</c> are permissions (<c>order:read</c>), everything else is a role
/// (<c>admin</c>, <c>user</c>). This splits them into <see cref="ClaimTypes.Role"/> claims and
/// <c>permission</c> claims so that standard <c>RequireRole</c> / <c>RequireClaim</c> policies work.
/// </summary>
public static class KeycloakClaimsTransformer
{
    public static void Apply(ClaimsPrincipal principal, string apiClientId)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var resourceAccess = identity.FindFirst("resource_access")?.Value;
        if (string.IsNullOrEmpty(resourceAccess))
        {
            return;
        }

        using var doc = JsonDocument.Parse(resourceAccess);
        if (!doc.RootElement.TryGetProperty(apiClientId, out var client) ||
            !client.TryGetProperty("roles", out var roles) ||
            roles.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in roles.EnumerateArray())
        {
            if (entry.GetString() is not { Length: > 0 } name)
            {
                continue;
            }

            identity.AddClaim(IsPermission(name)
                ? new Claim(Claims.Permission, name)
                : new Claim(ClaimTypes.Role, name));
        }
    }

    /// <summary>Convention: <c>resource:action</c> names are permissions, plain names are roles.</summary>
    public static bool IsPermission(string roleName) => roleName.Contains(':');
}
