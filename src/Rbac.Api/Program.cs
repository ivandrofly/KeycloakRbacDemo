using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Rbac.Api;

var builder = WebApplication.CreateBuilder(args);

var keycloak = builder.Configuration.GetSection("Keycloak").Get<KeycloakOptions>()
               ?? throw new InvalidOperationException("Missing 'Keycloak' configuration section.");

builder.Services.AddOpenApi();
builder.Services.AddSingleton(keycloak);
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<AuditLog>();

// --- Authentication: validate Keycloak JWTs -------------------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = keycloak.Authority;          // http://localhost:8180/realms/rbac-demo
        options.Audience = keycloak.Audience;            // rbac-api (added to tokens by the audience mapper)
        options.RequireHttpsMetadata = false;            // local Docker Keycloak serves plain HTTP
        options.BackchannelHttpHandler = Ipv4Http.CreateHandler(); // see Ipv4Http.cs
        options.MapInboundClaims = false;                // keep Keycloak claim names as-is
        options.TokenValidationParameters.NameClaimType = "preferred_username";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

        // Keycloak puts roles in nested JSON (realm_access / resource_access).
        // Flatten them into standard role + "permission" claims once per request.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                KeycloakClaimsTransformer.Apply(ctx.Principal!, keycloak.Audience);
                return Task.CompletedTask;
            }
        };
    });

// --- Authorization: policies expressed in plain ASP.NET Core terms --------------
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.AdminOnly,   p => p.RequireRole(Roles.Admin))
    .AddPolicy(Policies.OrderRead,   p => p.RequireClaim(Claims.Permission, Permissions.OrderRead))
    .AddPolicy(Policies.OrderWrite,  p => p.RequireClaim(Claims.Permission, Permissions.OrderWrite))
    .AddPolicy(Policies.OrderDelete, p => p.RequireClaim(Claims.Permission, Permissions.OrderDelete));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

// --- /api/me : any authenticated user -------------------------------------------
app.MapGet("/api/me", (ClaimsPrincipal user) => Results.Ok(new MeResponse(
        user.Identity!.Name!,
        user.FindAll(ClaimTypes.Role).Select(c => c.Value).Order().ToArray(),
        user.FindAll(Claims.Permission).Select(c => c.Value).Order().ToArray())))
    .RequireAuthorization()
    .WithName("GetMe");

// --- /api/orders : permission based ---------------------------------------------
var orders = app.MapGroup("/api/orders");

orders.MapGet("/", (OrderStore store) => Results.Ok(store.All()))
    .RequireAuthorization(Policies.OrderRead)
    .WithName("ListOrders");

orders.MapPost("/", (CreateOrderRequest req, OrderStore store, AuditLog audit, ClaimsPrincipal user) =>
    {
        if (string.IsNullOrWhiteSpace(req.Customer) || req.Amount <= 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["customer"] = string.IsNullOrWhiteSpace(req.Customer) ? ["Customer is required."] : [],
                ["amount"]   = req.Amount <= 0 ? ["Amount must be greater than zero."] : []
            });
        }

        var order = store.Add(req.Customer.Trim(), req.Amount);
        audit.Record(user.Identity!.Name!, "order.created", $"#{order.Id} {order.Customer} {order.Amount:0.00}");
        return Results.Created($"/api/orders/{order.Id}", order);
    })
    .RequireAuthorization(Policies.OrderWrite)
    .WithName("CreateOrder");

orders.MapDelete("/{id:int}", (int id, OrderStore store, AuditLog audit, ClaimsPrincipal user) =>
    {
        if (!store.Remove(id, out var removed))
        {
            return Results.NotFound();
        }
        audit.Record(user.Identity!.Name!, "order.deleted", $"#{removed!.Id} {removed.Customer}");
        return Results.NoContent();
    })
    .RequireAuthorization(Policies.OrderDelete)
    .WithName("DeleteOrder");

// --- /api/admin : role based ----------------------------------------------------
app.MapGet("/api/admin/audit", (AuditLog audit) => Results.Ok(audit.All()))
    .RequireAuthorization(Policies.AdminOnly)
    .WithName("GetAuditLog");

app.Run();
