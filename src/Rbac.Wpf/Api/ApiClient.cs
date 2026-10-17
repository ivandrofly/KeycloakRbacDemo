using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Rbac.Wpf.Api;

public sealed record MeResponse(string UserName, string[] Roles, string[] Permissions);
public sealed record Order(int Id, string Customer, decimal Amount, DateTimeOffset CreatedAt);
public sealed record AuditEntry(DateTimeOffset At, string User, string Action, string Details);

/// <summary>Outcome of an API call; non-success responses are data, not exceptions, so the UI can show them.</summary>
public sealed record ApiResult<T>(HttpStatusCode StatusCode, T? Value, string? Error)
{
    public bool IsSuccess => (int)StatusCode is >= 200 and < 300;
    public string StatusText => $"{(int)StatusCode} {StatusCode}";
}

public sealed class ApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new(Auth.Ipv4Http.CreateHandler()) { BaseAddress = new Uri("http://localhost:5100/") };

    public void SetAccessToken(string? token) =>
        _http.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);

    public Task<ApiResult<MeResponse>> GetMeAsync() => SendAsync<MeResponse>(HttpMethod.Get, "api/me");
    public Task<ApiResult<List<Order>>> GetOrdersAsync() => SendAsync<List<Order>>(HttpMethod.Get, "api/orders");
    public Task<ApiResult<Order>> CreateOrderAsync(string customer, decimal amount) =>
        SendAsync<Order>(HttpMethod.Post, "api/orders", new { customer, amount });
    public Task<ApiResult<object>> DeleteOrderAsync(int id) => SendAsync<object>(HttpMethod.Delete, $"api/orders/{id}");
    public Task<ApiResult<List<AuditEntry>>> GetAuditAsync() => SendAsync<List<AuditEntry>>(HttpMethod.Get, "api/admin/audit");

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);

        try
        {
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new ApiResult<T>(response.StatusCode, default, await response.Content.ReadAsStringAsync());

            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                return new ApiResult<T>(response.StatusCode, default, null);

            var value = await response.Content.ReadFromJsonAsync<T>(Json);
            return new ApiResult<T>(response.StatusCode, value, null);
        }
        catch (HttpRequestException ex)
        {
            return new ApiResult<T>(HttpStatusCode.ServiceUnavailable, default, $"API unreachable: {ex.Message}");
        }
    }
}
