# Keycloak RBAC Demo (.NET 10 API + WPF)

A small, end-to-end demo of role-based access control:

- **Keycloak** issues tokens that carry the user's roles and permissions.
- **Rbac.Api** (ASP.NET Core minimal API) enforces them with ordinary `RequireRole` / `RequireClaim` policies.
- **Rbac.Wpf** signs in through the system browser (Authorization Code + PKCE) and shows or hides UI
  depending on what the token says the user may do. The server still enforces every rule.

```
keycloak/realm-export.json   realm, roles, clients, mappers, test users
keycloak/setup-realm.ps1     imports the realm via the Keycloak Admin REST API
scripts/smoke-test.ps1       calls every endpoint as every user and checks the status codes
src/Rbac.Api                 the protected API           -> http://localhost:5100
src/Rbac.Wpf                 the desktop client
```

## Authorization model

Everything is a **client role on `rbac-api`**; the realm itself carries nothing app-specific.
Two kinds of role are told apart by name:

| Kind | Naming | Values | In the token |
|---|---|---|---|
| Who you are | plain name | `admin`, `user` | `resource_access.rbac-api.roles` |
| What you may do | `resource:action` | `order:read`, `order:write`, `order:delete` | `resource_access.rbac-api.roles` |

`admin` and `user` are **composite** roles: `admin` grants all three permissions, `user` grants `order:read`.
Permissions can also be assigned directly to a user (carol has `order:write` on top of `user`).

The API's `KeycloakClaimsTransformer` reads that one list once per request and turns plain names into standard
role claims and `resource:action` names into `permission` claims. Everything after that is vanilla ASP.NET Core
authorization (`RequireRole("admin")`, `RequireClaim("permission", "order:read")`).

### Endpoints

| Endpoint | Requires |
|---|---|
| `GET  /api/me` | any signed-in user |
| `GET  /api/orders` | permission `order:read` |
| `POST /api/orders` | permission `order:write` |
| `DELETE /api/orders/{id}` | permission `order:delete` |
| `GET  /api/admin/audit` | role `admin` |

### Test users (password = username)

| User | Role | Extra permission | Sees in the WPF app |
|---|---|---|---|
| alice | admin | | orders, new-order form, delete buttons, admin audit log |
| carol | user | `order:write` | orders, new-order form |
| bob | user | | orders, read-only |

## Keycloak clients

| Client | Type | Used by |
|---|---|---|
| `rbac-api` | bearer-only | the API: owns all roles (`admin`, `user`, `order:*`) and is the token audience (`aud`) |
| `rbac-wpf` | public, PKCE S256, redirect `http://127.0.0.1:7890/*` | the WPF app |
| `rbac-test-cli` | public, **password grant** | `scripts/smoke-test.ps1` and `Rbac.Api.http` only. Test convenience, delete it for anything real. |

## Running it

1. **Keycloak** must be running on `http://localhost:8180` (see `D:\docker\keycloak\docker-compose.yml`).
   Always use `localhost`, not `127.0.0.1`, so the token issuer matches the API's authority.

2. **Create the realm** (asks for the admin password, or set `$env:KC_ADMIN_PASSWORD`):

   ```powershell
   .\keycloak\setup-realm.ps1            # add -Recreate to drop and re-import
   ```

3. **Run the API**:

   ```powershell
   dotnet run --project src/Rbac.Api
   ```

4. **Run the WPF app** and click *Sign in with Keycloak*. Your browser opens the Keycloak login page;
   after signing in, the browser redirects to the app's loopback listener on port 7890.

   ```powershell
   dotnet run --project src/Rbac.Wpf
   ```

5. Optional: **smoke-test the API** without the UI:

   ```powershell
   .\scripts\smoke-test.ps1
   ```

   Expected result:

   | Endpoint | no token | bob | carol | alice |
   |---|---|---|---|---|
   | GET /api/me | 401 | 200 | 200 | 200 |
   | GET /api/orders | 401 | 200 | 200 | 200 |
   | POST /api/orders | 401 | 403 | 201 | 201 |
   | DELETE /api/orders/{id} | 401 | 403 | 403 | 204 |
   | GET /api/admin/audit | 401 | 403 | 403 | 200 |

## Troubleshooting

- **Login hangs on "Waiting for you to sign in"** or the API never validates tokens: Docker Desktop publishes
  8180 on both `127.0.0.1` and `[::1]`, and on some Windows hosts the IPv6 forward silently drops packets while
  .NET tries `[::1]` first for `localhost`. Both apps therefore route the Keycloak back-channel through
  `Ipv4Http.cs`, which connects over IPv4 only. If you remove it and see hangs, that is why.
- **401 from the API after login**: the token issuer must equal `Keycloak:Authority` in `appsettings.json`.
  Open Keycloak and the apps through the same host name (`localhost`).
- **`setup-realm.ps1` fails with 401 mid-way**: the admin token lives 60 s; the script fetches a fresh one
  before the import, but a very slow Keycloak can still exceed it. Just re-run.

## Things to try

- Sign in as **bob**, then press *Call admin endpoint anyway*. The button is visible to everyone on purpose:
  the API answers `403 Forbidden`, proving the server enforces the rules even when a client ignores the UI.
- In the Keycloak admin console (realm `rbac-demo` → Users → bob → Role mapping → Assign role → filter by
  clients) assign `rbac-api order:write`, sign out and back in: the *New order* form appears.
- Add a new permission (e.g. `order:approve`) as a client role on `rbac-api` (Clients → rbac-api → Roles), add it
  to the `admin` composite, then add a policy and endpoint in `Program.cs` and a `CanApproveOrders` flag in
  `MainViewModel`.
- Add a new role (e.g. `auditor`) the same way; because it has no `:` it becomes a role claim, usable with
  `RequireRole("auditor")`.
