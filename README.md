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
src/Rbac.Cli                 console app: asks Keycloak Authorization Services for permission decisions
```

## Authorization model

Everything is a **client role on `rbac-api`**; the realm itself carries nothing app-specific.
Two kinds of role are told apart by name:

| Kind | Naming | Values | In the token |
|---|---|---|---|
| Who you are | plain name | `admin`, `user` | `resource_access.rbac-api.roles` |
| What you may do | `resource:action` | `order:read`, `order:write`, `order:delete` | `resource_access.rbac-api.roles` |

`admin` and `user` are **composite** roles: `admin` grants all three permissions, `user` grants `order:read`.

Users do not get roles directly. They are members of a **group**, and the group carries the role mapping:

| Group | Client roles on `rbac-api` |
|---|---|
| `/admins` | `admin` |
| `/users` | `user` |

Permissions can still be assigned directly to a user on top of the group (carol has `order:write` in addition to
`/users`). The token looks exactly the same whichever path a role came from.

> **Note:** Keycloak evaluates effective roles as the union of direct roles, group roles and composites, so you can
> mix the approaches. For a clean demo I'd move all three users to groups and keep carol's extra permission as a
> direct assignment to show both paths.

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

### Test users

| User | Password | Group | Extra permission | Effective access | Sees in the WPF app |
|---|---|---|---|---|---|
| alice | `alice` | `/admins` | | `order:read`, `order:write`, `order:delete`, admin endpoints | orders, new-order form, delete buttons, admin audit log |
| carol | `carol` | `/users` | `order:write` | `order:read`, `order:write` | orders, new-order form |
| bob | `bob` | `/users` | | `order:read` | orders, read-only |

These users exist only in the `rbac-demo` realm. The Keycloak admin console (realm `master`) uses the admin account
from your Docker compose environment and is not touched by this project.

## Keycloak clients

| Client | Type | Used by |
|---|---|---|
| `rbac-api` | bearer-only | the API: owns all roles (`admin`, `user`, `order:*`) and is the token audience (`aud`) |
| `rbac-wpf` | public, PKCE S256, redirect `http://127.0.0.1:7890/*` | the WPF app |
| `rbac-test-cli` | public, **password grant** | `scripts/smoke-test.ps1` and `Rbac.Api.http` only. Test convenience, delete it for anything real. |
| `rbac-console` | confidential (secret `rbac-console-secret`), **password grant**, **Authorization Services enabled** | `src/Rbac.Cli`. The resource server for the [Authorization Services](#authorization-services-keycloak-side-permission-decisions) section. The fixed secret and the password grant are test conveniences, same caveat as above. |

## Authorization Services (Keycloak-side permission decisions)

The API above decides on its own by reading roles out of the token. Keycloak can also make the decision for you:
a client with **Authorization Services** enabled becomes a *resource server* that knows its resources, the
scopes (actions) on them, and the policies that grant them. A caller presents a user's access token and asks
"may this user do `scope` on `resource`?"; Keycloak answers yes/no or hands back a *Requesting Party Token*
(RPT) whose `authorization.permissions` claim lists everything the user may do.

The `rbac-console` client is set up this way in `keycloak/realm-export.json` (`authorizationSettings`). It reuses
the `rbac-api` roles, so the two decision layers always agree:

| Resource | Scopes | Permission | Granted by policy |
|---|---|---|---|
| `Orders` (`/api/orders/*`) | `order:read` | *Read orders* | *Has order:read* (client role `rbac-api/order:read`) |
| | `order:write` | *Write orders* | *Has order:write* (client role `rbac-api/order:write`) |
| | `order:delete` | *Delete orders* | *Delete orders* (client role `rbac-api/order:delete`) |
| `Audit log` (`/api/admin/audit`) | `audit:read` | *Read audit log* | *Is admin* (client role `rbac-api/admin`) |

The role policies read the roles from the user's token, which is why `rbac-console` has `fullScopeAllowed` and the
`roles` scope: the token must carry `resource_access.rbac-api.roles` or every policy denies.

All of it goes through the ordinary token endpoint with the UMA ticket grant:

```
POST /realms/rbac-demo/protocol/openid-connect/token
Authorization: Bearer <user access token>

grant_type=urn:ietf:params:oauth:grant-type:uma-ticket
&audience=rbac-console                       # the resource server whose policies are evaluated
&permission=Orders#order:write               # optional: ask about one resource#scope
&response_mode=decision                      # decision -> {"result":true} or 403 access_denied
                                             # permissions -> JSON list of granted (resource, scopes)
                                             # omitted -> a full RPT (JWT with an "authorization" claim)
```

`src/Rbac.Cli` does exactly that for every test user and compares the answers with what the groups and composites
should produce. It exits with 1 if any decision differs and 2 if Keycloak or the client is missing:

```powershell
dotnet run --project src/Rbac.Cli
```

| Permission | alice | carol | bob |
|---|---|---|---|
| `Orders#order:read` | GRANT | GRANT | GRANT |
| `Orders#order:write` | GRANT | GRANT | DENY |
| `Orders#order:delete` | GRANT | DENY | DENY |
| `Audit log#audit:read` | GRANT | DENY | DENY |

It also prints alice's decoded RPT so you can see the `authorization.permissions` claim an API would check if it
delegated the decision to Keycloak. Settings come from `KEYCLOAK_URL`, `KEYCLOAK_REALM`, `KEYCLOAK_CLIENT_ID` and
`KEYCLOAK_CLIENT_SECRET` (defaults match this repo).

> Existing realms do not pick the client up automatically: run `.\keycloak\setup-realm.ps1 -Recreate` once.

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

5. Optional: **check Keycloak's own permission decisions** with the console app (see the section above):

   ```powershell
   dotnet run --project src/Rbac.Cli
   ```

6. Optional: **smoke-test the API** without the UI:

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
- **One 401 right after `setup-realm.ps1 -Recreate`** while the API keeps running: re-creating the realm rotates
  its signing keys and the API still has the old ones cached. The first request with an unknown key id fails and
  triggers a refresh; everything after that succeeds. Restart the API or just retry.
- **`setup-realm.ps1` fails with 401 mid-way**: the admin token lives 60 s; the script fetches a fresh one
  before the import, but a very slow Keycloak can still exceed it. Just re-run.

## Things to try

- Sign in as **bob**, then press *Call admin endpoint anyway*. The button is visible to everyone on purpose:
  the API answers `403 Forbidden`, proving the server enforces the rules even when a client ignores the UI.
- In the Keycloak admin console (realm `rbac-demo` → Users → bob → Groups → Join Group) add bob to `/admins`,
  sign out and back in: the delete buttons and the admin panel appear. No role was touched, only membership.
- Or assign a single permission directly (Users → bob → Role mapping → Assign role → filter by clients →
  `rbac-api order:write`): the *New order* form appears while the rest stays read-only.
- Add a new permission (e.g. `order:approve`) as a client role on `rbac-api` (Clients → rbac-api → Roles), add it
  to the `admin` composite, then add a policy and endpoint in `Program.cs` and a `CanApproveOrders` flag in
  `MainViewModel`.
- Add a new role (e.g. `auditor`) the same way; because it has no `:` it becomes a role claim, usable with
  `RequireRole("auditor")`.
- Open Clients → rbac-console → Authorization → Evaluate, pick bob and the `Orders` resource, and compare the result
  with what `src/Rbac.Cli` prints. Then edit the *Write orders* permission to also apply the *Is admin* policy and
  re-run the console app: nothing changes for alice, and carol now gets DENY because the decision strategy is
  *unanimous*.
