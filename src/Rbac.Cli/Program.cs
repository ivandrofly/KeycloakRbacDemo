using System.Text.Json;
using Rbac.Cli;

// Checks Keycloak Authorization Services on the "rbac-console" client: logs every test user in, asks Keycloak
// which resource/scope permissions each one holds, and compares yes/no decisions against the expected matrix.
// Exit code: 0 = all decisions as expected, 1 = at least one mismatch, 2 = Keycloak/realm not set up.

const string ApiRolesClient = "rbac-api";   // the client whose roles the role policies look at

var settings = KeycloakSettings.FromEnvironment();
var keycloak = new KeycloakClient(settings);

// Expected decisions, derived from the realm: alice -> /admins (admin), carol -> /users (user) + order:write, bob -> /users (user).
var permissions = new[] { "Orders#order:read", "Orders#order:write", "Orders#order:delete", "Audit log#audit:read" };
var expected = new Dictionary<string, bool[]>
{
    //                 read   write  delete audit
    ["alice"] = [true,  true,  true,  true ],
    ["carol"] = [true,  true,  false, false],
    ["bob"]   = [true,  false, false, false]
};

Console.WriteLine($"Keycloak : {settings.Url}");
Console.WriteLine($"Realm    : {settings.Realm}");
Console.WriteLine($"Client   : {settings.ClientId} (resource server with Authorization Services)");

var discovery = await keycloak.DiscoverAsync();
if (!discovery.IsSuccess) return Fail(2, discovery.Error!);
Console.WriteLine($"Token endpoint: {discovery.Value}");

var mismatches = 0;
var summary = new List<(string User, bool?[] Actual)>();

foreach (var (user, expectations) in expected)
{
    Console.WriteLine();
    Console.WriteLine($"=== {user} ===");

    // 1. Log in (password grant, test-only) and show the roles Keycloak put in the token.
    var login = await keycloak.PasswordGrantAsync(user, user);
    if (!login.IsSuccess)
    {
        var hint = login.Error!.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
            ? " (client missing: re-import the realm with .\\keycloak\\setup-realm.ps1 -Recreate)" : "";
        return Fail(2, $"Login failed for {user}: {login.Error}{hint}");
    }
    var token = login.Value!;
    using (var payload = Jwt.DecodePayload(token))
    {
        var roles = Jwt.ClientRoles(payload.RootElement, ApiRolesClient);
        Console.WriteLine($"Token roles ({ApiRolesClient}): {string.Join(", ", roles)}");
    }

    // 2. What does Authorization Services say the user may access?
    var granted = await keycloak.GetPermissionsAsync(token);
    if (granted.IsSuccess)
        foreach (var p in granted.Value!.OrderBy(p => p.Resource))
            Console.WriteLine($"Granted: {p.Resource} -> {string.Join(", ", (p.Scopes ?? []).Order())}");
    else if (granted.StatusCode == System.Net.HttpStatusCode.Forbidden)
        Console.WriteLine("Granted: (nothing)");
    else
        return Fail(2, $"Permission query failed: {granted.Error}");

    // 3. One yes/no decision per resource#scope, compared with what the realm should produce.
    var actual = new bool?[permissions.Length];
    for (var i = 0; i < permissions.Length; i++)
    {
        var decision = await keycloak.DecideAsync(token, permissions[i]);
        if (!decision.IsSuccess && decision.StatusCode != System.Net.HttpStatusCode.Forbidden)
            return Fail(2, $"Decision for {permissions[i]} failed: {decision.Error}");

        actual[i] = decision.Value;
        var ok = decision.Value == expectations[i];
        if (!ok) mismatches++;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {permissions[i],-22} {(decision.Value ? "GRANT" : "DENY")}" +
                          (ok ? "" : $"  (expected {(expectations[i] ? "GRANT" : "DENY")})"));
    }
    summary.Add((user, actual));

    // 4. For the admin, show what a full RPT looks like: the token Keycloak would hand to a resource server.
    if (user == "alice")
    {
        var rpt = await keycloak.GetRptAsync(token);
        if (rpt.IsSuccess)
        {
            using var payload = Jwt.DecodePayload(rpt.Value!);
            var authz = payload.RootElement.GetProperty("authorization");
            Console.WriteLine("RPT authorization claim:");
            Console.WriteLine(JsonSerializer.Serialize(authz, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

Console.WriteLine();
Console.WriteLine($"{"Permission",-22} {string.Join(" ", expected.Keys.Select(u => $"{u,-6}"))}");
for (var i = 0; i < permissions.Length; i++)
    Console.WriteLine($"{permissions[i],-22} {string.Join(" ", summary.Select(s => $"{(s.Actual[i] == true ? "GRANT" : "DENY"),-6}"))}");

Console.WriteLine();
if (mismatches == 0)
{
    Console.WriteLine("All decisions match the expected matrix.");
    return 0;
}
Console.WriteLine($"{mismatches} decision(s) differ from the expected matrix.");
return 1;

static int Fail(int code, string message)
{
    Console.Error.WriteLine($"ERROR: {message}");
    return code;
}
