# Simple RBAC sample

The smallest possible version of this repo's idea: a .NET 10 minimal API that trusts Keycloak tokens and gates
endpoints by **realm role**, plus a console app that logs the test users in and calls every endpoint.

```
simple-demo-realm.json   realm "simple-demo": roles admin/user, client simple-api, users admin/user
Simple.Api               the API            -> http://localhost:5200
Simple.Console           calls the API as every user and prints the status codes
Simple.Console/Simple.http  same requests for the IDE HTTP client (Rider / VS / VS Code REST Client)
```

## Run it

Keycloak must be running on `http://localhost:8180` (the same Docker instance the main README uses).

```powershell
# 1. import the realm (asks for the Keycloak admin password unless $env:KC_ADMIN_PASSWORD is set)
.\keycloak\setup-realm.ps1 -RealmFile .\simple\simple-demo-realm.json     # add -Recreate to re-import

# 2. start the API
dotnet run --project simple/Simple.Api

# 3. in another terminal, run the console
dotnet run --project simple/Simple.Console
```

Expected console output:

```
user      /public  /me      /user    /admin
anonymous 200      401      401      401
user      200      200      200      403
admin     200      200      200      200
```

## Endpoints

| Endpoint | Requires |
|---|---|
| `GET /public` | nothing |
| `GET /me` | any signed-in user, returns name and roles |
| `GET /user` | realm role `user` |
| `GET /admin` | realm role `admin` |

## Test users (realm `simple-demo`, password = username)

| User | Realm roles |
|---|---|
| `admin` | `admin`, `user` |
| `user` | `user` |

## How it works

- `simple-api` is a public client with the password grant enabled, so the console can log a user in with one POST.
  An audience mapper adds `simple-api` to the token's `aud`, which the API validates.
- Keycloak puts realm roles in the token as `realm_access.roles`. The API's `OnTokenValidated` copies each one into
  a standard role claim, after which `RequireRole("admin")` is plain ASP.NET Core.
- Both apps talk to Keycloak via `127.0.0.1` instead of `localhost`. Docker Desktop's IPv6 loopback forward can
  hang, and using the IPv4 address on both sides keeps the token issuer identical to what the API discovers.

Get a token by hand:

```bash
curl -s -X POST http://127.0.0.1:8180/realms/simple-demo/protocol/openid-connect/token \
  -d grant_type=password -d client_id=simple-api -d username=admin -d password=admin

curl -s http://localhost:5200/admin -H "Authorization: Bearer <access_token>"
```
