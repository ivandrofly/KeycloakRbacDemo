using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rbac.Cli;

/// <param name="ClientId">Client used to log the test users in (password grant).</param>
/// <param name="Audience">Resource server whose Authorization Services settings are evaluated; defaults to <paramref name="ClientId"/>.</param>
public sealed record KeycloakSettings(string Url, string Realm, string ClientId, string ClientSecret, string Audience)
{
    public static KeycloakSettings FromEnvironment()
    {
        var clientId = Environment.GetEnvironmentVariable("KEYCLOAK_CLIENT_ID") ?? "rbac-console";
        return new(
            Environment.GetEnvironmentVariable("KEYCLOAK_URL")?.TrimEnd('/') ?? "http://localhost:8180",
            Environment.GetEnvironmentVariable("KEYCLOAK_REALM") ?? "rbac-demo",
            clientId,
            Environment.GetEnvironmentVariable("KEYCLOAK_CLIENT_SECRET") ?? "rbac-console-secret",
            Environment.GetEnvironmentVariable("KEYCLOAK_AUDIENCE") ?? clientId);
    }
}

/// <summary>One granted permission as returned by the UMA grant with <c>response_mode=permissions</c>.</summary>
public sealed record GrantedPermission(
    [property: JsonPropertyName("rsname")] string Resource,
    [property: JsonPropertyName("scopes")] string[]? Scopes);

/// <summary>Outcome of a Keycloak call; errors are data so the caller can print them and keep going.</summary>
public sealed record KeycloakResult<T>(HttpStatusCode StatusCode, T? Value, string? Error)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
}

/// <summary>
/// Minimal client for the two things this demo needs from Keycloak: the password grant to log a test user in,
/// and the UMA ticket grant on the same token endpoint, which is how Keycloak Authorization Services answers
/// "may this user access resource X with scope Y?".
/// </summary>
public sealed class KeycloakClient
{
    private const string UmaTicketGrant = "urn:ietf:params:oauth:grant-type:uma-ticket";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new(Ipv4Http.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly KeycloakSettings _settings;
    private string? _tokenEndpoint;

    public KeycloakClient(KeycloakSettings settings) => _settings = settings;

    public string Issuer => $"{_settings.Url}/realms/{_settings.Realm}";
    public string TokenEndpoint => _tokenEndpoint ?? throw new InvalidOperationException("Call DiscoverAsync first.");

    /// <summary>Reads the UMA discovery document and remembers the token endpoint.</summary>
    public async Task<KeycloakResult<string>> DiscoverAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{Issuer}/.well-known/uma2-configuration");
            if (!response.IsSuccessStatusCode)
            {
                return new(response.StatusCode, null, await response.Content.ReadAsStringAsync());
            }

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
            _tokenEndpoint = doc!.RootElement.GetProperty("token_endpoint").GetString();
            return new(response.StatusCode, _tokenEndpoint, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(HttpStatusCode.ServiceUnavailable, null, $"Keycloak unreachable at {_settings.Url}: {ex.Message}");
        }
    }

    /// <summary>Resource Owner Password grant against the confidential client (test convenience only).</summary>
    public Task<KeycloakResult<string>> PasswordGrantAsync(string username, string password) =>
        PostTokenAsync(null, new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["username"] = username,
            ["password"] = password
        }, json => json.GetProperty("access_token").GetString()!);

    /// <summary>Asks Authorization Services which (resource, scopes) pairs the user may access. No RPT is issued.</summary>
    public Task<KeycloakResult<GrantedPermission[]>> GetPermissionsAsync(string userToken) =>
        PostTokenAsync(userToken, UmaGrant(("response_mode", "permissions")),
            json => json.Deserialize<GrantedPermission[]>(Json)!);

    /// <summary>
    /// Yes/no decision for one <c>resource#scope</c>. Keycloak answers 200 <c>{"result":true}</c> when granted and
    /// 403 <c>{"error":"access_denied"}</c> when not; the 403 is returned as <c>false</c>, not as an error.
    /// </summary>
    public async Task<KeycloakResult<bool>> DecideAsync(string userToken, string permission)
    {
        var result = await PostTokenAsync(userToken, UmaGrant(("response_mode", "decision"), ("permission", permission)),
            json => json.GetProperty("result").GetBoolean());

        return result.StatusCode == HttpStatusCode.Forbidden ? new(result.StatusCode, false, null) : result;
    }

    /// <summary>Full UMA grant: returns the Requesting Party Token, a JWT carrying an <c>authorization.permissions</c> claim.</summary>
    public Task<KeycloakResult<string>> GetRptAsync(string userToken) =>
        PostTokenAsync(userToken, UmaGrant(), json => json.GetProperty("access_token").GetString()!);

    private Dictionary<string, string> UmaGrant(params (string Key, string Value)[] extra)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = UmaTicketGrant,
            ["audience"] = _settings.Audience   // the resource server whose resources/policies are evaluated
        };
        foreach (var (key, value) in extra)
        {
            form[key] = value;
        }

        return form;
    }

    private async Task<KeycloakResult<T>> PostTokenAsync<T>(string? bearer, Dictionary<string, string> form, Func<JsonElement, T> read)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        try
        {
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return new(response.StatusCode, default, DescribeError(body));
            }

            using var doc = JsonDocument.Parse(body);
            return new(response.StatusCode, read(doc.RootElement), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(HttpStatusCode.ServiceUnavailable, default, $"Keycloak unreachable: {ex.Message}");
        }
    }

    private static string DescribeError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = doc.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            var text = string.Join(": ", new[] { error, description }.Where(s => !string.IsNullOrEmpty(s)));
            return text.Length > 0 ? text : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }
}

public static class Jwt
{
    /// <summary>Decodes the payload of a JWT without validating it; we only display what Keycloak issued.</summary>
    public static JsonDocument DecodePayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload));
    }

    /// <summary>Reads <c>resource_access.{client}.roles</c> from an access token payload.</summary>
    public static string[] ClientRoles(JsonElement payload, string client) =>
        payload.TryGetProperty("resource_access", out var ra) &&
        ra.TryGetProperty(client, out var c) &&
        c.TryGetProperty("roles", out var roles)
            ? roles.EnumerateArray().Select(r => r.GetString()!).Order().ToArray()
            : [];
}
