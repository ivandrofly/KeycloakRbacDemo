using System.Text.Json;
using Duende.IdentityModel.OidcClient;

namespace Rbac.Wpf.Auth;

/// <summary>Snapshot of the signed-in user, derived from the Keycloak access token.</summary>
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
    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Authorization Code + PKCE login against Keycloak using the system browser.</summary>
public sealed class AuthService
{
    private const string Authority = "http://localhost:8180/realms/rbac-demo";
    private const string ClientId = "rbac-wpf";
    private const string ApiClientId = "rbac-api";
    private const int LoopbackPort = 7890;

    private readonly OidcClient _client = new(new OidcClientOptions
    {
        Authority = Authority,
        ClientId = ClientId,
        Scope = "openid profile email",
        RedirectUri = $"http://127.0.0.1:{LoopbackPort}/callback",
        PostLogoutRedirectUri = $"http://127.0.0.1:{LoopbackPort}/signout",
        Browser = new SystemBrowser(LoopbackPort),
        BackchannelHandler = Ipv4Http.CreateHandler(),        // see Ipv4Http.cs
        Policy = { Discovery = { RequireHttps = false } },   // local Docker Keycloak is plain HTTP
        LoadProfile = false                                   // we only need the access token claims
    });

    public async Task<UserSession> LoginAsync(CancellationToken ct = default)
    {
        var result = await _client.LoginAsync(new LoginRequest(), ct);
        if (result.IsError)
            throw new InvalidOperationException($"Login failed: {result.Error} {result.ErrorDescription}".Trim());

        return BuildSession(result.AccessToken, result.RefreshToken, result.IdentityToken, result.AccessTokenExpiration);
    }

    public async Task<UserSession> RefreshAsync(UserSession session, CancellationToken ct = default)
    {
        if (session.RefreshToken is null) return session;

        var result = await _client.RefreshTokenAsync(session.RefreshToken, cancellationToken: ct);
        if (result.IsError)
            throw new InvalidOperationException($"Token refresh failed: {result.Error}");

        return BuildSession(result.AccessToken, result.RefreshToken, session.IdentityToken, result.AccessTokenExpiration);
    }

    public async Task LogoutAsync(UserSession session, CancellationToken ct = default)
    {
        await _client.LogoutAsync(new LogoutRequest { IdTokenHint = session.IdentityToken }, ct);
    }

    /// <summary>
    /// Reads roles/permissions from the access token payload. This is purely for shaping the UI;
    /// the API re-validates the same token and enforces the rules server-side.
    /// </summary>
    private static UserSession BuildSession(string accessToken, string? refreshToken, string? idToken, DateTimeOffset expiresAt)
    {
        using var payload = JsonDocument.Parse(DecodeJwtPayload(accessToken));
        var root = payload.RootElement;

        var userName = root.TryGetProperty("preferred_username", out var u) ? u.GetString() ?? "?" : "?";
        var roles = ReadRoles(root, "realm_access");
        var permissions = root.TryGetProperty("resource_access", out var ra) ? ReadRoles(ra, ApiClientId) : [];

        return new UserSession(accessToken, refreshToken, idToken, expiresAt, userName, roles, permissions);
    }

    private static IReadOnlyList<string> ReadRoles(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var obj) ||
            !obj.TryGetProperty("roles", out var roles) ||
            roles.ValueKind != JsonValueKind.Array)
            return [];

        return roles.EnumerateArray().Select(r => r.GetString()!).Where(r => r.Length > 0).Order().ToList();
    }

    private static byte[] DecodeJwtPayload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(part.PadRight(part.Length + (4 - part.Length % 4) % 4, '='));
    }
}
