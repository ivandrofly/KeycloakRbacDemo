# Rbac.Cli

Console app (.NET 10, no NuGet packages) that checks **Keycloak Authorization Services** from the outside. For each
test user it logs in, asks Keycloak which permissions the user holds, requests one yes/no decision per
`resource#scope`, and compares the answers with the matrix the realm should produce. Useful after editing
policies or permissions in the admin console.

Realm setup and the Authorization Services section are in the [root README](../../README.md).

## What it does

1. Reads `{issuer}/.well-known/uma2-configuration` to find the token endpoint.
2. Logs `alice`, `carol` and `bob` in with the **password grant** through the confidential `rbac-console`
   client (test convenience only) and prints the `rbac-api` roles in each token.
3. Sends the UMA grant with `response_mode=permissions` and prints every granted `(resource, scopes)` pair.
4. Sends `response_mode=decision` for `Orders#order:read`, `Orders#order:write`, `Orders#order:delete` and
   `Audit log#audit:read`, prints `OK`/`FAIL` against the expected matrix.
5. For alice, requests a full RPT and prints its `authorization` claim.

Expected matrix:

| Permission | alice | carol | bob |
|---|---|---|---|
| `Orders#order:read` | GRANT | GRANT | GRANT |
| `Orders#order:write` | GRANT | GRANT | DENY |
| `Orders#order:delete` | GRANT | DENY | DENY |
| `Audit log#audit:read` | GRANT | DENY | DENY |

Exit codes: `0` all decisions as expected, `1` at least one mismatch, `2` Keycloak unreachable or the realm/client
is missing (re-import with `.\keycloak\setup-realm.ps1 -Recreate`).

## Configuration (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `KEYCLOAK_URL` | `http://localhost:8180` | Keycloak base URL |
| `KEYCLOAK_REALM` | `rbac-demo` | realm |
| `KEYCLOAK_CLIENT_ID` | `rbac-console` | client used to log the users in |
| `KEYCLOAK_CLIENT_SECRET` | `rbac-console-secret` | its secret |
| `KEYCLOAK_AUDIENCE` | same as the client id | **resource server whose Authorization settings are evaluated** |

Three clients in the realm carry the same demo resources, scopes and policies, so the audience picks which one
to check:

```powershell
dotnet run --project src/Rbac.Cli                                        # rbac-console
$env:KEYCLOAK_AUDIENCE = 'rbac-api'; dotnet run --project src/Rbac.Cli   # the API's settings
$env:KEYCLOAK_AUDIENCE = 'rbac-wpf'; dotnet run --project src/Rbac.Cli   # the WPF client's settings
```

## How the UMA grant works

Every call is a POST to the ordinary token endpoint with the user's access token as bearer:

```
grant_type=urn:ietf:params:oauth:grant-type:uma-ticket
audience=<resource server client id>
permission=Orders#order:write          # optional, one resource#scope
response_mode=decision | permissions   # omitted -> a full RPT (JWT with an "authorization" claim)
```

`decision` answers `200 {"result":true}` or `403 access_denied`; the client maps the 403 to `false` instead of an
error. No client credentials travel with the UMA call: Keycloak identifies the client from the token's `azp`.

## Files

| File | Role |
|---|---|
| `Program.cs` | the user loop, expected matrix, output and exit code |
| `KeycloakClient.cs` | discovery, password grant, `GetPermissionsAsync`, `DecideAsync`, `GetRptAsync`, JWT payload helper |
| `Ipv4Http.cs` | IPv4-only handler (Docker Desktop's IPv6 loopback can hang) |
