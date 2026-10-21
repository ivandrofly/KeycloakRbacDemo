# Rbac.Api

ASP.NET Core minimal API (.NET 10) protected by Keycloak JWTs. It is the *resource server* of the RBAC demo: it
validates the access token, turns the `rbac-api` client roles into ordinary ASP.NET Core claims, and enforces
`RequireRole` / `RequireClaim` policies. It never calls Keycloak for a decision; everything it needs is in the token.

Listens on `http://localhost:5100` (`Properties/launchSettings.json`). Realm setup, test users and the overall
picture are in the [root README](../../README.md).

## Endpoints

| Method | Path | Policy | Who passes |
|---|---|---|---|
| GET | `/api/me` | authenticated | everyone with a valid token; returns user name, roles, permissions |
| GET | `/api/orders` | `OrderRead` (`permission=order:read`) | alice, carol, bob |
| POST | `/api/orders` | `OrderWrite` (`permission=order:write`) | alice, carol |
| DELETE | `/api/orders/{id}` | `OrderDelete` (`permission=order:delete`) | alice |
| GET | `/api/admin/audit` | `AdminOnly` (`role=admin`) | alice |

Orders and the audit log are in-memory (`Stores.cs`), seeded with three orders and reset on every restart.
`/openapi/v1.json` is mapped in Development.

## How a request is authorized

1. `AddJwtBearer` validates the token against `Keycloak:Authority` (issuer, signature via the discovery document)
   and `Keycloak:Audience` (`rbac-api`, which the WPF and test clients add to the token with an audience mapper).
2. `OnTokenValidated` runs `KeycloakClaimsTransformer.Apply`. Keycloak puts client roles in nested JSON
   (`resource_access.rbac-api.roles`); the transformer flattens that list using one naming convention:
   names with a `:` (`order:read`) become `permission` claims, plain names (`admin`, `user`) become role claims.
3. The policies in `Program.cs` are plain `RequireRole` / `RequireClaim` checks against those claims
   (constants in `Models.cs`).

Adding a permission therefore means: create the client role on `rbac-api` in Keycloak (and add it to a composite
such as `admin`), add a constant and a policy, and put `.RequireAuthorization(...)` on the endpoint.

## Configuration

`appsettings.json`:

```json
"Keycloak": {
  "Authority": "http://localhost:8180/realms/rbac-demo",
  "Audience": "rbac-api"
}
```

`RequireHttpsMetadata` is off because the local Docker Keycloak is plain HTTP. `MapInboundClaims` is off so claim
names stay as Keycloak issues them (`preferred_username` is the name claim).

## Run and test

```powershell
dotnet run --project src/Rbac.Api
.\scripts\smoke-test.ps1          # every endpoint as every user, checks the status codes
```

`Rbac.Api.http` has the same calls for the IDE's HTTP client; it fetches a token through the test-only
`rbac-test-cli` client (password grant, password = username).

## Files

| File | Role |
|---|---|
| `Program.cs` | authentication, policies, endpoints |
| `KeycloakClaimsTransformer.cs` | `resource_access` → role and `permission` claims |
| `Models.cs` | options, constants (roles, permissions, policies), DTOs |
| `Stores.cs` | in-memory `OrderStore` and `AuditLog` |
| `Ipv4Http.cs` | back-channel handler that connects over IPv4 only (Docker Desktop's IPv6 loopback can hang) |

## Troubleshooting

- **401 for every request**: the token's `iss` must equal `Keycloak:Authority` exactly. Open Keycloak and the apps
  through `localhost`, not `127.0.0.1`.
- **One 401 right after `setup-realm.ps1 -Recreate`**: the realm's signing keys rotated and the API still has the
  old ones cached. It refreshes on the first unknown key id; retry or restart the API.
- **Hangs talking to Keycloak**: that is what `Ipv4Http.cs` prevents; keep it on the bearer options.
