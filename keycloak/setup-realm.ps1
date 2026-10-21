<#
.SYNOPSIS
    Creates a realm in a running Keycloak from a realm export file (default: realm-export.json -> "rbac-demo").

.EXAMPLE
    .\setup-realm.ps1 -AdminPassword 'secret'
    .\setup-realm.ps1 -Recreate                                   # drop and re-import the realm
    .\setup-realm.ps1 -RealmFile .\consent-demo-realm.json        # the second realm (src/Consent.Wpf)
#>
[CmdletBinding()]
param(
    [string] $KeycloakUrl   = 'http://localhost:8180',
    [string] $AdminUser     = 'admin',
    [string] $AdminPassword = $env:KC_ADMIN_PASSWORD,
    [string] $RealmFile     = (Join-Path $PSScriptRoot 'realm-export.json'),
    [switch] $Recreate
)

$ErrorActionPreference = 'Stop'
$KeycloakUrl = $KeycloakUrl.TrimEnd('/')

if (-not $AdminPassword) {
    $secure = Read-Host -Prompt "Keycloak admin password for '$AdminUser'" -AsSecureString
    $AdminPassword = [System.Net.NetworkCredential]::new('', $secure).Password
}

$realmJson = Get-Content $RealmFile -Raw | ConvertFrom-Json
$realm = $realmJson.realm
Write-Host "Keycloak : $KeycloakUrl"
Write-Host "Realm    : $realm"

# --- admin token (short-lived: 60 s by default, so fetch one per step) -----
function Get-AdminHeaders {
    $tokenResponse = Invoke-RestMethod -Method Post -NoProxy `
        -Uri "$KeycloakUrl/realms/master/protocol/openid-connect/token" `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = $AdminUser; password = $AdminPassword }
    @{ Authorization = "Bearer $($tokenResponse.access_token)" }
}
$headers = Get-AdminHeaders

# --- does the realm already exist? -----------------------------------------
$exists = $true
try {
    Invoke-RestMethod -NoProxy -Headers $headers -Uri "$KeycloakUrl/admin/realms/$realm" | Out-Null
} catch {
    if ($_.Exception.Response.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) { $exists = $false } else { throw }
}

if ($exists) {
    if (-not $Recreate) {
        Write-Host "Realm '$realm' already exists. Re-run with -Recreate to drop and re-import it." -ForegroundColor Yellow
        return
    }
    Write-Host "Deleting existing realm '$realm'..." -ForegroundColor Yellow
    Invoke-RestMethod -Method Delete -NoProxy -Headers $headers -Uri "$KeycloakUrl/admin/realms/$realm" | Out-Null
}

# --- import ----------------------------------------------------------------
Write-Host "Importing realm from $RealmFile ..."
$headers = Get-AdminHeaders
Invoke-RestMethod -Method Post -NoProxy -Headers $headers `
    -Uri "$KeycloakUrl/admin/realms" `
    -ContentType 'application/json' `
    -Body (Get-Content $RealmFile -Raw) | Out-Null

$issuer = (Invoke-RestMethod -NoProxy -Uri "$KeycloakUrl/realms/$realm/.well-known/openid-configuration").issuer
Write-Host ""
Write-Host "Realm created. Issuer: $issuer" -ForegroundColor Green
Write-Host "Admin console: $KeycloakUrl/admin/master/console/#/$realm"
Write-Host ""
Write-Host "Test users (password = username), from ${RealmFile}:"
$realmJson.users | ForEach-Object {
    $clientRoles = @()
    if ($_.clientRoles) {
        foreach ($p in $_.clientRoles.PSObject.Properties) { $clientRoles += ($p.Value | ForEach-Object { "$($p.Name)/$_" }) }
    }
    [pscustomobject]@{
        User        = $_.username
        Name        = "$($_.firstName) $($_.lastName)".Trim()
        Email       = $_.email
        Groups      = ($_.groups -join ', ')
        ClientRoles = ($clientRoles -join ', ')
    }
} | Format-Table -AutoSize
