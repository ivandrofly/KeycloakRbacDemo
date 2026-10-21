# Rbac.Wpf

WPF desktop client (.NET 10) for the RBAC demo. It signs the user in with Keycloak through the system browser,
asks **Keycloak Authorization Services** what the user may do, shapes its UI from that answer, and calls
`Rbac.Api` with the access token. The API still enforces every rule; the UI gating is a convenience.

Realm setup, test users and the overall picture are in the [root README](../../README.md).

## Sign-in flow

- Keycloak client `rbac-wpf` in realm `rbac-demo`: Authorization Code + PKCE (S256), redirect to a loopback
  listener on `http://127.0.0.1:7890/callback`, **consent required**, login theme `keycloak.v2`.
- The client is **confidential** (secret `rbac-wpf-secret`, embedded in `AuthService.cs`) only because Keycloak
  allows Authorization Services on confidential clients alone. A secret inside a desktop app protects nothing; it
  is a demo convenience, not a pattern to copy.
- `SystemBrowser.cs` opens the default browser and waits on a raw `TcpListener` for the redirect, so no URL ACL
  or admin rights are needed. On the first login Keycloak shows the consent page; it remembers the answer.
- Login, refresh and logout go through `Duende.IdentityModel.OidcClient` (`Auth/AuthService.cs`).

## Where the permissions come from

After login (and after every token refresh) `KeycloakAuthzClient.cs` sends the user's access token to the token
endpoint with the UMA grant:

```
grant_type=urn:ietf:params:oauth:grant-type:uma-ticket
audience=rbac-wpf
response_mode=permissions
```

Keycloak evaluates the resources, scopes and policies configured on the `rbac-wpf` client (Authorization tab) and
answers with the granted `(resource, scopes)` pairs. The app turns them into `Resource#scope` strings and the
view model binds visibility to them:

| Flag | Granted permission | Shows |
|---|---|---|
| `CanReadOrders` | `Orders#order:read` | the Orders card |
| `CanWriteOrders` | `Orders#order:write` | the new-order form |
| `CanDeleteOrders` | `Orders#order:delete` | the Delete column |
| `IsAdmin` | `Audit log#audit:read` (policy *Is admin*) | the audit-log card |

The roles in the token are still shown as chips, but nothing is derived from them any more.
*Call admin endpoint anyway* is visible to everyone on purpose: it proves the API answers 403 regardless of the UI.

## Run

```powershell
dotnet run --project src/Rbac.Wpf     # needs Keycloak with the rbac-demo realm and Rbac.Api on :5100
```

Sign in as `alice` (admin), `carol` (user + order:write) or `bob` (user); password = username.

## Files

| File | Role |
|---|---|
| `Auth/AuthService.cs` | OidcClient options (authority, client id/secret, scopes, port), builds the `UserSession` |
| `Auth/KeycloakAuthzClient.cs` | UMA grant call; 403 `access_denied` = nothing granted |
| `Auth/SystemBrowser.cs` | system browser + loopback listener (port 7890) |
| `Auth/Ipv4Http.cs` | IPv4-only handler for the Keycloak and API back-channels |
| `Api/ApiClient.cs` | typed calls to `Rbac.Api`, `ApiResult<T>` with status text |
| `ViewModels/MainViewModel.cs` | commands, capability flags, status bar |
| `MainWindow.xaml`, `App.xaml` | layout, cards, chips, shared styles |

## Troubleshooting

- **`invalid_client` / `unauthorized_client` at login**: the realm in Keycloak predates the confidential
  `rbac-wpf`; re-import with `.\keycloak\setup-realm.ps1 -Recreate` and restart the API.
- **Signed in, but no permission chips**: Authorization Services answered `access_denied`. Check the user's
  `rbac-api` roles and the policies under Clients → rbac-wpf → Authorization; `Rbac.Cli` with
  `KEYCLOAK_AUDIENCE=rbac-wpf` prints the same decisions from the console.
- **The browser never redirects back**: port 7890 must be free, and the redirect URI must stay in the client's
  list. The listener gives up after 5 minutes.
- **To see the consent page again**: revoke the consent in the Account console
  (`http://localhost:8180/realms/rbac-demo/account`, Applications) or re-import the realm.
