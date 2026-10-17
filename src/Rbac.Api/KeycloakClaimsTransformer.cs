using System.Security.Claims;
using System.Text.Json;

namespace Rbac.Api;

/// <summary>
/// Keycloak emits roles as nested JSON:
/// <code>
///   "realm_access":    { "roles": ["admin", ...] }
///   "resource_access": { "rbac-api": { "roles": ["order:read", ...] } }
/// </code>
/// This flattens realm roles into <see cref="ClaimTypes.Role"/> claims and the
/// API client's roles into <c>permission</c> claims so that standard
/// <c>RequireRole</c> / <c>RequireClaim</c> policies work.
/// </summary>
public static class KeycloakClaimsTransformer
{
    public static void Apply(ClaimsPrincipal principal, string apiClientId)
    {
        if (principal.Identity is not ClaimsIdentity identity) return;

        foreach (var role in ReadRoles(identity.FindFirst("realm_access")?.Value))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        var resourceAccess = identity.FindFirst("resource_access")?.Value;
        if (resourceAccess is null) return;

        using var doc = JsonDocument.Parse(resourceAccess);
        if (doc.RootElement.TryGetProperty(apiClientId, out var client))
        {
            foreach (var permission in ReadRoles(client.GetRawText()))
            {
                identity.AddClaim(new Claim(Claims.Permission, permission));
            }
        }
    }

    private static IEnumerable<string> ReadRoles(string? json)
    {
        if (string.IsNullOrEmpty(json)) yield break;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var role in roles.EnumerateArray())
        {
            if (role.GetString() is { Length: > 0 } value) yield return value;
        }
    }
}
