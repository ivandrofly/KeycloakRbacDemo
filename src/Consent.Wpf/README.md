# Consent.Wpf

WPF desktop app (.NET 10) whose only subject is **OAuth consent** in Keycloak. It lives in its own realm,
`consent-demo` (`keycloak/consent-demo-realm.json`), with no roles, no API and no Authorization Services, so it
can be run and reset without touching the RBAC demo. The overall picture is in the [root README](../../README.md).

## The realm

| Piece | Setting |
|---|---|
| Client `consent-wpf` | public, Authorization Code + PKCE S256, redirect `http://127.0.0.1:7891/*`, **Consent required**, login theme `keycloak.v2`, consent screen text *Consent Demo desktop app* |
| Default client scopes | `basic`, `profile`, `email`, `roles`: always applied, whether or not the app names them |
| Optional client scopes | `address`, `phone`, `offline_access`: only applied when the app puts them in `scope` |
| Users | `dave`, `erin` (password = username) with name, email, `phoneNumber` and the address attributes the built-in `phone` and `address` mappers read |
| User profile | `unmanagedAttributePolicy: ENABLED` so those attributes survive the import and are editable in the admin console |

Import it once:

```powershell
.\keycloak\setup-realm.ps1 -RealmFile .\keycloak\consent-demo-realm.json   # add -Recreate to re-import
```

## What the app shows

1. **Signed out**: tick which optional scopes to request. The default scopes are listed but fixed.
2. **Sign in with Keycloak**: the keycloak.v2 login page, then the **consent page** listing exactly the requested
   scopes (User profile, Email address, User roles, plus Address / Phone number / Offline access when ticked)
   and the client's consent text. Keycloak stores the decision per user and client.
3. **Signed in**: the granted scopes (the `scope` claim of the access token) as chips, and every ID-token claim,
   user claims first (`name`, `email`, `phone_number`, the nested `address` object, ...), protocol claims last.
   `roles` and `basic` never show up in the `scope` claim: those built-in scopes have *Include in token scope*
   off, although they are on the consent page.
4. Signing in again does **not** ask: the stored consent covers it. It asks again only for a scope that was not
   granted before (tick `offline_access` on a later run to see the page come back for that one scope).
5. **Revoke consent** calls the Keycloak account REST API with the user's own token and then signs out locally,
   so the next sign-in shows the consent page again:

   ```
   DELETE /realms/consent-demo/account/applications/consent-wpf/consent
   Authorization: Bearer <access token>
   Accept: application/json
   ```

   The `Accept` header matters: without it Keycloak routes `/account/*` to the account console SPA and answers
   405. The `roles` default scope is what puts `account` into the token's `aud` and the `manage-account` role
   into it, which the account API requires.
6. **Open account console** opens `/realms/consent-demo/account/#/applications`, where the user can see and
   revoke the same consent without the app.

## Run

```powershell
dotnet run --project src/Consent.Wpf   # needs Keycloak with the consent-demo realm; no API
```

It can run side by side with `Rbac.Wpf`: different realm, client and loopback port (7891 vs 7890).

## Files

| File | Role |
|---|---|
| `Auth/ConsentAuthService.cs` | login with the chosen scopes, logout, `RevokeConsentAsync`, `OpenAccountConsole`, token decoding |
| `Auth/SystemBrowser.cs` | system browser + loopback listener (port 7891), copied from Rbac.Wpf |
| `Auth/Ipv4Http.cs` | IPv4-only handler, copied from Rbac.Wpf |
| `ViewModels/MainViewModel.cs` | scope pickers, session state, commands |
| `MainWindow.xaml`, `App.xaml` | layout and the shared styles |

## Troubleshooting

- **No consent page on login**: the user already consented. Use *Revoke consent*, the account console, or
  re-import the realm with `-Recreate`.
- **`address` / `phone_number` claims missing**: the user has no such attributes, or the scope was not ticked.
  Check Users → dave → Attributes in the admin console; the realm file sets them for dave and erin.
- **Revoke answers 405 or HTML**: the `Accept: application/json` header is missing (see above).
- **Underscores vanish from a checkbox label**: WPF treats `_` in `Content` as an access-key marker; the labels
  here are TextBlocks for that reason.
