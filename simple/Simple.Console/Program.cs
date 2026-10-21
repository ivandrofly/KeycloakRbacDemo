using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

// Logs each test user in (password grant), calls every endpoint of Simple.Api and prints the status codes.
// Exit code: 0 = every status as expected, 1 = at least one mismatch, 2 = Keycloak or the API is unreachable.

var keycloakUrl = (Environment.GetEnvironmentVariable("KEYCLOAK_URL") ?? "http://127.0.0.1:8180").TrimEnd('/');
var realm       = Environment.GetEnvironmentVariable("KEYCLOAK_REALM") ?? "simple-demo";
var clientId    = Environment.GetEnvironmentVariable("KEYCLOAK_CLIENT_ID") ?? "simple-api";
var apiUrl      = (Environment.GetEnvironmentVariable("API_URL") ?? "http://localhost:5200").TrimEnd('/');
var tokenUrl    = $"{keycloakUrl}/realms/{realm}/protocol/openid-connect/token";

string[] endpoints = ["/public", "/me", "/user", "/admin"];

// username -> expected status per endpoint (password = username; "anonymous" sends no token)
var expected = new Dictionary<string, HttpStatusCode[]>
{
    ["anonymous"] = [HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized],
    ["user"]      = [HttpStatusCode.OK, HttpStatusCode.OK,           HttpStatusCode.OK,           HttpStatusCode.Forbidden],
    ["admin"]     = [HttpStatusCode.OK, HttpStatusCode.OK,           HttpStatusCode.OK,           HttpStatusCode.OK]
};

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

Console.WriteLine($"Keycloak : {tokenUrl}");
Console.WriteLine($"API      : {apiUrl}");
Console.WriteLine();
Console.WriteLine($"{"user",-10}{string.Join("", endpoints.Select(e => $"{e,-9}"))}");

var mismatches = 0;
foreach (var (user, statuses) in expected)
{
    string? token = null;
    if (user != "anonymous")
    {
        try
        {
            token = await GetTokenAsync(user, user);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Fail(2, $"Keycloak unreachable or login failed for '{user}': {ex.Message}");
        }
    }

    var line = $"{user,-10}";
    for (var i = 0; i < endpoints.Length; i++)
    {
        HttpStatusCode actual;
        try
        {
            actual = await CallApiAsync(endpoints[i], token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Fail(2, $"API unreachable at {apiUrl}: {ex.Message}");
        }

        var ok = actual == statuses[i];
        if (!ok)
        {
            mismatches++;
        }

        line += $"{(int)actual}{(ok ? "" : "!")}".PadRight(9);
    }

    Console.WriteLine(line);
}

Console.WriteLine();
if (mismatches > 0)
{
    return Fail(1, $"{mismatches} unexpected status code(s), marked with '!'.");
}

Console.WriteLine("All status codes as expected.");
return 0;

async Task<string> GetTokenAsync(string username, string password)
{
    using var response = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "password",
        ["client_id"] = clientId,
        ["username"] = username,
        ["password"] = password
    }));

    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new HttpRequestException($"{(int)response.StatusCode} {body}");
    }

    using var json = JsonDocument.Parse(body);
    return json.RootElement.GetProperty("access_token").GetString()!;
}

async Task<HttpStatusCode> CallApiAsync(string path, string? token)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl + path);
    if (token is not null)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    using var response = await http.SendAsync(request);
    return response.StatusCode;
}

static int Fail(int code, string message)
{
    Console.Error.WriteLine($"ERROR: {message}");
    return code;
}
