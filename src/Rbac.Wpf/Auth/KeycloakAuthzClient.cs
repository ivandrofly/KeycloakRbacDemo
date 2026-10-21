using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rbac.Wpf.Auth;

/// <summary>One granted permission as returned by the UMA grant with <c>response_mode=permissions</c>.</summary>
public sealed record GrantedPermission(
    [property: JsonPropertyName("rsname")] string Resource,
    [property: JsonPropertyName("scopes")] string[]? Scopes);

/// <summary>
/// Asks Keycloak Authorization Services which (resource, scopes) pairs a user may access on a resource server.
/// It is the UMA ticket grant on the ordinary token endpoint, sent with the user's access token as bearer;
/// Keycloak identifies the client from the token (<c>azp</c>), so no client credentials travel with the request.
/// Same mechanism as <c>src/Rbac.Cli/KeycloakClient.cs</c>, reduced to the one call the UI needs.
/// </summary>
public sealed class KeycloakAuthzClient
{
    private const string UmaTicketGrant = "urn:ietf:params:oauth:grant-type:uma-ticket";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new(Ipv4Http.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _issuer;
    private readonly string _audience;
    private string? _tokenEndpoint;

    /// <param name="issuer">Realm URL, e.g. <c>http://localhost:8180/realms/rbac-demo</c>.</param>
    /// <param name="audience">Client id of the resource server whose Authorization Services settings are evaluated.</param>
    public KeycloakAuthzClient(string issuer, string audience)
    {
        _issuer = issuer.TrimEnd('/');
        _audience = audience;
    }

    /// <summary>
    /// Everything the user may do on the resource server. A 403 <c>access_denied</c> means "nothing granted"
    /// and is returned as an empty list; any other failure throws with Keycloak's error text.
    /// </summary>
    public async Task<IReadOnlyList<GrantedPermission>> GetPermissionsAsync(string userAccessToken, CancellationToken ct = default)
    {
        var endpoint = await GetTokenEndpointAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = UmaTicketGrant,
                ["audience"] = _audience,
                ["response_mode"] = "permissions"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userAccessToken);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Keycloak permission query failed ({(int)response.StatusCode}): {DescribeError(body)}");
        }

        return JsonSerializer.Deserialize<GrantedPermission[]>(body, Json) ?? [];
    }

    private async Task<string> GetTokenEndpointAsync(CancellationToken ct)
    {
        if (_tokenEndpoint is not null)
        {
            return _tokenEndpoint;
        }

        using var response = await _http.GetAsync($"{_issuer}/.well-known/uma2-configuration", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"UMA discovery failed ({(int)response.StatusCode}): {DescribeError(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return _tokenEndpoint = doc.RootElement.GetProperty("token_endpoint").GetString()
            ?? throw new InvalidOperationException("UMA discovery document has no token_endpoint.");
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
