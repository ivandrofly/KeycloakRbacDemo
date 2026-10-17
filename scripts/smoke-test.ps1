<#
.SYNOPSIS
    Exercises every API endpoint as every test user and checks the expected HTTP status codes.
    Requires the realm (keycloak/setup-realm.ps1) and the API (dotnet run --project src/Rbac.Api) to be running.
#>
[CmdletBinding()]
param(
    [string] $ApiUrl      = 'http://localhost:5100',
    [string] $KeycloakUrl = 'http://localhost:8180',
    [string] $Realm       = 'rbac-demo'
)

$ErrorActionPreference = 'Stop'
$tokenUrl = "$($KeycloakUrl.TrimEnd('/'))/realms/$Realm/protocol/openid-connect/token"

function Get-Token([string] $user) {
    # rbac-test-cli has the password grant enabled for tests only; the WPF app uses PKCE instead.
    $body = "grant_type=password&client_id=rbac-test-cli&username=$user&password=$user"
    (curl.exe -s -X POST $tokenUrl -H 'Content-Type: application/x-www-form-urlencoded' -d $body | ConvertFrom-Json).access_token
}

function Invoke-Api([string] $method, [string] $path, [string] $token, [string] $json) {
    $args = @('-s', '-o', 'NUL', '-w', '%{http_code}', '-X', $method, "$ApiUrl$path")
    if ($token) { $args += @('-H', "Authorization: Bearer $token") }
    if ($json)  { $args += @('-H', 'Content-Type: application/json', '-d', $json) }
    [int](curl.exe @args)
}

$tokens = @{ none = $null }
foreach ($u in 'bob', 'carol', 'alice') { $tokens[$u] = Get-Token $u; if (-not $tokens[$u]) { throw "Could not get token for $u" } }

$newOrder = '{"customer":"Smoke Test","amount":12.34}'

# Expected matrix: (method, path, body) -> status per user. Delete uses order ids 1..3 seeded by the API.
$cases = @(
    @{ Name = 'GET  /api/me';            Method = 'GET';    Path = '/api/me';          Expect = @{ none = 401; bob = 200; carol = 200; alice = 200 } },
    @{ Name = 'GET  /api/orders';        Method = 'GET';    Path = '/api/orders';      Expect = @{ none = 401; bob = 200; carol = 200; alice = 200 } },
    @{ Name = 'POST /api/orders';        Method = 'POST';   Path = '/api/orders';      Body = $newOrder; Expect = @{ none = 401; bob = 403; carol = 201; alice = 201 } },
    @{ Name = 'DEL  /api/orders/{id}';   Method = 'DELETE'; Path = '/api/orders/1';    Expect = @{ none = 401; bob = 403; carol = 403; alice = 204 } },
    @{ Name = 'GET  /api/admin/audit';   Method = 'GET';    Path = '/api/admin/audit'; Expect = @{ none = 401; bob = 403; carol = 403; alice = 200 } }
)

$failures = 0
$rows = foreach ($c in $cases) {
    $row = [ordered]@{ Endpoint = $c.Name }
    foreach ($u in 'none', 'bob', 'carol', 'alice') {
        $actual = Invoke-Api $c.Method $c.Path $tokens[$u] $c.Body
        $ok = $actual -eq $c.Expect[$u]
        if (-not $ok) { $failures++ }
        $row[$u] = if ($ok) { "$actual" } else { "$actual (expected $($c.Expect[$u]))" }
    }
    [pscustomobject]$row
}

$rows | Format-Table -AutoSize
if ($failures -eq 0) { Write-Host "All checks passed." -ForegroundColor Green } else { Write-Host "$failures check(s) FAILED." -ForegroundColor Red; exit 1 }
