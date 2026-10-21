using System.Text.Json;
using Duende.IdentityModel.OidcClient;

namespace Rbac.Wpf.Auth;

/// <summary>
/// Snapshot of the signed-in user. <see cref="Roles"/> come from the access token
/// (<c>resource_access.rbac-api.roles</c>); <see cref="Permissions"/> are what Keycloak Authorization Services
/// granted on the <c>rbac-wpf</c> resource server, as <c>Resource#scope</c> pairs (e.g. <c>Orders#order:read</c>).
/// </summary>
public sealed record UserSession(
    string AccessToken,
    string? RefreshToken,
    string? IdentityToken,
    DateTimeOffset ExpiresAt,
    string UserName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions)
{
    public bool HasRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>Exact <c>Resource#scope</c> match, e.g. <c>"Orders#order:write"</c>.</summary>
    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when any resource grants the scope, e.g. <c>"order:write"</c>.</summary>
    public bool HasScope(string scope) =>
        Permissions.Any(p => p.EndsWith("#" + scope, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Authorization Code + PKCE login against Keycloak using the system browser, followed by a call to
/// Keycloak Authorization Services to learn what the user may do.
/// </summary>
public sealed class AuthService
{
    private const string Authority = "http://localhost:8180/realms/rbac-demo";
    private const string ClientId = "rbac-wpf";
    // rbac-wpf is confidential because Keycloak only allows Authorization Services on confidential clients.
    // A secret inside a desktop app is not a secret; this is a demo convenience (see README).
    private const string ClientSecret = "rbac-wpf-secret";
    private const string ApiClientId = "rbac-api";   // the client whose roles show up in the token
    private const int LoopbackPort = 7890;

    private readonly OidcClient _client = new(new OidcClientOptions
    {
        Authority = Authority,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        Scope = "openid profile email",
        RedirectUri = $"http://127.0.0.1:{LoopbackPort}/callback",
        PostLogoutRedirectUri = $"http://127.0.0.1:{LoopbackPort}/signout",
        Browser = new SystemBrowser(LoopbackPort),
        BackchannelHandler = Ipv4Http.CreateHandler(),        // see Ipv4Http.cs
        Policy = { Discovery = { RequireHttps = false } },   // local Docker Keycloak is plain HTTP
        LoadProfile = false                                   // we only need the access token claims
    });

    // Evaluates the Authorization Services settings of rbac-wpf itself (audience = our own client id).
    private readonly KeycloakAuthzClient _authz = new(Authority, ClientId);

    public async Task<UserSession> LoginAsync(CancellationToken ct = default)
    {
        var result = await _client.LoginAsync(new LoginRequest(), ct);
        if (result.IsError)
        {
            throw new InvalidOperationException($"Login failed: {result.Error} {result.ErrorDescription}".Trim());
        }

        return await BuildSessionAsync(result.AccessToken, result.RefreshToken, result.IdentityToken, result.AccessTokenExpiration, ct);
    }

    public async Task<UserSession> RefreshAsync(UserSession session, CancellationToken ct = default)
    {
        if (session.RefreshToken is null)
        {
            return session;
        }

        var result = await _client.RefreshTokenAsync(session.RefreshToken, cancellationToken: ct);
        if (result.IsError)
        {
            throw new InvalidOperationException($"Token refresh failed: {result.Error}");
        }

        return await BuildSessionAsync(result.AccessToken, result.RefreshToken, session.IdentityToken, result.AccessTokenExpiration, ct);
    }

    public async Task LogoutAsync(UserSession session, CancellationToken ct = default)
    {
        await _client.LogoutAsync(new LogoutRequest { IdTokenHint = session.IdentityToken }, ct);
    }

    /// <summary>
    /// Roles are read from the access token payload for display. Permissions are not derived from the token:
    /// the token is sent to Keycloak with the UMA grant and Keycloak evaluates the resources, scopes and policies
    /// configured on <c>rbac-wpf</c>. This shapes the UI only; the API re-validates the same token and enforces
    /// the rules server-side.
    /// </summary>
    private async Task<UserSession> BuildSessionAsync(string accessToken, string? refreshToken, string? idToken,
        DateTimeOffset expiresAt, CancellationToken ct)
    {
        string userName;
        IReadOnlyList<string> apiRoles;
        using (var payload = JsonDocument.Parse(DecodeJwtPayload(accessToken)))
        {
            var root = payload.RootElement;
            userName = root.TryGetProperty("preferred_username", out var u) ? u.GetString() ?? "?" : "?";
            apiRoles = root.TryGetProperty("resource_access", out var ra) ? ReadClientRoles(ra, ApiClientId) : [];
        }

        var granted = await _authz.GetPermissionsAsync(accessToken, ct);
        var permissions = granted
            .SelectMany(g => (g.Scopes ?? []).Select(s => $"{g.Resource}#{s}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList();

        return new UserSession(accessToken, refreshToken, idToken, expiresAt, userName, apiRoles, permissions);
    }

    private static IReadOnlyList<string> ReadClientRoles(JsonElement resourceAccess, string clientId)
    {
        if (!resourceAccess.TryGetProperty(clientId, out var client) ||
            !client.TryGetProperty("roles", out var roles) ||
            roles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return roles.EnumerateArray().Select(r => r.GetString()!).Where(r => r.Length > 0).Order().ToList();
    }

    private static byte[] DecodeJwtPayload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(part.PadRight(part.Length + (4 - part.Length % 4) % 4, '='));
    }
}
