using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

// Keycloak realm "simple-demo" (see ../simple-demo-realm.json). 127.0.0.1 on purpose: see ../README.md.
var authority = builder.Configuration["Keycloak:Authority"] ?? "http://127.0.0.1:8180/realms/simple-demo";
var audience  = builder.Configuration["Keycloak:Audience"]  ?? "simple-api";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = false;                       // local Keycloak is plain HTTP
        options.MapInboundClaims = false;                           // keep Keycloak claim names as-is
        options.TokenValidationParameters.NameClaimType = "preferred_username";
        options.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

        // Keycloak puts realm roles in a nested JSON claim: "realm_access": { "roles": [ "admin", "user" ] }.
        // Copy each one into a standard role claim so RequireRole(...) just works.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
                var realmAccess = identity.FindFirst("realm_access")?.Value;
                if (realmAccess is null)
                {
                    return Task.CompletedTask;
                }

                using var json = JsonDocument.Parse(realmAccess);
                if (json.RootElement.TryGetProperty("roles", out var roles))
                {
                    foreach (var role in roles.EnumerateArray())
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, role.GetString()!));
                    }
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/public", () => Results.Ok(new { message = "Hello, anyone." }));

app.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
    {
        name = user.Identity!.Name,
        roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Order().ToArray()
    }))
    .RequireAuthorization();

app.MapGet("/user", (ClaimsPrincipal user) => Results.Ok(new { message = $"Hello, {user.Identity!.Name}. You are a user." }))
    .RequireAuthorization(p => p.RequireRole("user"));

app.MapGet("/admin", (ClaimsPrincipal user) => Results.Ok(new { message = $"Hello, {user.Identity!.Name}. You are an admin." }))
    .RequireAuthorization(p => p.RequireRole("admin"));

app.Run();
