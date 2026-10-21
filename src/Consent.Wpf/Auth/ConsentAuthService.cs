using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Duende.IdentityModel.OidcClient;

namespace Consent.Wpf.Auth;

/// <summary>One claim from the ID token, flattened for display.</summary>
public sealed record ClaimRow(string Name, string Value);

/// <summary>
/// Snapshot of the signed-in user: the tokens, the scopes Keycloak actually granted (the <c>scope</c> claim of the
/// access token) and the ID-token claims those scopes produced.
/// </summary>
public sealed record ConsentSession(
    string AccessToken,
    string? RefreshToken,
    string? IdentityToken,
    DateTimeOffset ExpiresAt,
    string UserName,
    IReadOnlyList<string> GrantedScopes,
    IReadOnlyList<ClaimRow> IdTokenClaims);

/// <summary>
/// Authorization Code + PKCE login against the <c>consent-demo</c> realm with a public client, plus the two
/// consent-specific calls: revoking the stored consent through the Keycloak account REST API, and opening the
/// account console where the user can do the same by hand.
/// </summary>
public sealed class ConsentAuthService
{
    public const string Authority = "http://localhost:8180/realms/consent-demo";
    public const string ClientId = "consent-wpf";
    private const int LoopbackPort = 7891;

    /// <summary>Scopes every login asks for. They are default client scopes, so Keycloak adds them even if omitted.</summary>
    public static readonly string[] BaseScopes = ["openid", "profile", "email"];

    private readonly HttpClient _http = new(Ipv4Http.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<ConsentSession> LoginAsync(IEnumerable<string> optionalScopes, CancellationToken ct = default)
    {
        var scope = string.Join(' ', BaseScopes.Concat(optionalScopes).Distinct());

        // A new OidcClient per login because the requested scope is part of its options.
        var client = new OidcClient(new OidcClientOptions
        {
            Authority = Authority,
            ClientId = ClientId,
            Scope = scope,
            RedirectUri = $"http://127.0.0.1:{LoopbackPort}/callback",
            PostLogoutRedirectUri = $"http://127.0.0.1:{LoopbackPort}/signout",
            Browser = new SystemBrowser(LoopbackPort),
            BackchannelHandler = Ipv4Http.CreateHandler(),        // see Ipv4Http.cs
            Policy = { Discovery = { RequireHttps = false } },   // local Docker Keycloak is plain HTTP
            LoadProfile = false                                   // the ID token is all we need
        });

        var result = await client.LoginAsync(new LoginRequest(), ct);
        if (result.IsError)
        {
            throw new InvalidOperationException($"Login failed: {result.Error} {result.ErrorDescription}".Trim());
        }

        return BuildSession(result.AccessToken, result.RefreshToken, result.IdentityToken, result.AccessTokenExpiration);
    }

    public async Task LogoutAsync(ConsentSession session, CancellationToken ct = default)
    {
        var client = new OidcClient(new OidcClientOptions
        {
            Authority = Authority,
            ClientId = ClientId,
            PostLogoutRedirectUri = $"http://127.0.0.1:{LoopbackPort}/signout",
            Browser = new SystemBrowser(LoopbackPort),
            BackchannelHandler = Ipv4Http.CreateHandler(),
            Policy = { Discovery = { RequireHttps = false } }
        });
        await client.LogoutAsync(new LogoutRequest { IdTokenHint = session.IdentityToken }, ct);
    }

    /// <summary>
    /// Deletes the user's stored consent for this client (Keycloak account REST API,
    /// <c>DELETE /realms/{realm}/account/applications/{clientId}/consent</c>). The next login shows the
    /// consent page again. Needs the <c>roles</c> scope in the token: that is what puts <c>account</c> into
    /// <c>aud</c> and the <c>manage-account</c> role into the token.
    /// </summary>
    public async Task RevokeConsentAsync(ConsentSession session, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Authority}/account/applications/{ClientId}/consent");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        // Without this Keycloak routes /account/* to the account console SPA (HTML) instead of the REST API.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"Revoke consent failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
    }

    /// <summary>Opens the account console's Applications page, where consents are listed and can be revoked by hand.</summary>
    public static void OpenAccountConsole() =>
        Process.Start(new ProcessStartInfo($"{Authority}/account/#/applications") { UseShellExecute = true });

    private static ConsentSession BuildSession(string accessToken, string? refreshToken, string? idToken, DateTimeOffset expiresAt)
    {
        IReadOnlyList<string> scopes;
        string userName = "?";
        using (var access = JsonDocument.Parse(DecodeJwtPayload(accessToken)))
        {
            var root = access.RootElement;
            scopes = root.TryGetProperty("scope", out var s) && s.GetString() is { Length: > 0 } text
                ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order().ToList()
                : [];
            if (root.TryGetProperty("preferred_username", out var u))
            {
                userName = u.GetString() ?? "?";
            }
        }

        var claims = new List<ClaimRow>();
        if (idToken is not null)
        {
            using var id = JsonDocument.Parse(DecodeJwtPayload(idToken));
            foreach (var property in id.RootElement.EnumerateObject())
            {
                claims.Add(new ClaimRow(property.Name, Render(property.Value)));
            }
        }

        // User claims (the ones consent is about) first, the protocol plumbing last.
        claims = claims.OrderBy(c => TechnicalClaims.Contains(c.Name) ? 1 : 0).ToList();

        return new ConsentSession(accessToken, refreshToken, idToken, expiresAt, userName, scopes, claims);
    }

    private static readonly HashSet<string> TechnicalClaims = new(StringComparer.Ordinal)
    {
        "exp", "iat", "auth_time", "jti", "iss", "aud", "sub", "typ", "azp", "sid", "at_hash", "nonce", "acr", "session_state"
    };

    /// <summary>Scalars as text; the nested <c>address</c> object (and anything else structured) as compact JSON.</summary>
    private static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => "null",
        _ => value.GetRawText()
    };

    private static byte[] DecodeJwtPayload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(part.PadRight(part.Length + (4 - part.Length % 4) % 4, '='));
    }
}
