namespace Rbac.Api;

public sealed class KeycloakOptions
{
    public required string Authority { get; init; }
    public required string Audience { get; init; }
}

public static class Roles
{
    public const string Admin = "admin";
    public const string User = "user";
}

public static class Permissions
{
    public const string OrderRead = "order:read";
    public const string OrderWrite = "order:write";
    public const string OrderDelete = "order:delete";
}

public static class Claims
{
    public const string Permission = "permission";
}

public static class Policies
{
    public const string AdminOnly = "AdminOnly";
    public const string OrderRead = "OrderRead";
    public const string OrderWrite = "OrderWrite";
    public const string OrderDelete = "OrderDelete";
}

public sealed record MeResponse(string UserName, string[] Roles, string[] Permissions);

public sealed record Order(int Id, string Customer, decimal Amount, DateTimeOffset CreatedAt);

public sealed record CreateOrderRequest(string Customer, decimal Amount);

public sealed record AuditEntry(DateTimeOffset At, string User, string Action, string Details);
