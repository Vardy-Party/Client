<#
.SYNOPSIS
  Verify a Linux/Desktop build output has non-empty Auth0 ClientId/Domain in appsettings.json.

.DESCRIPTION
  After PatchAppSettings / merge-appsettings-secrets, the copied output appsettings.json
  must contain Auth0 Domain + ClientId. Does not print secret values — only EMPTY/NON_EMPTY.
#>
param(
    [Parameter(Mandatory = $true)][string]$AppSettingsPath
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $AppSettingsPath)) {
    throw "appsettings.json not found: $AppSettingsPath"
}

$appsettings = Get-Content -LiteralPath $AppSettingsPath -Raw -Encoding utf8 | ConvertFrom-Json
$clientId = [string]$appsettings.Auth0.ClientId
$domain = [string]$appsettings.Auth0.Domain

$clientState = if ([string]::IsNullOrWhiteSpace($clientId)) { 'EMPTY' } else { 'NON_EMPTY' }
$domainState = if ([string]::IsNullOrWhiteSpace($domain)) { 'EMPTY' } else { 'NON_EMPTY' }

Write-Host "[LINUX CHECK] $AppSettingsPath Auth0.ClientId=$clientState Auth0.Domain=$domainState"

if ($clientState -ne 'NON_EMPTY' -or $domainState -ne 'NON_EMPTY') {
    throw @"
Linux build output has empty Auth0 ClientId/Domain in appsettings.json.
Merge user-secrets before build (scripts/launch-linux-app.ps1 or -p:PatchAppSettings=true)
and do not git restore VardyParty.Linux/appsettings.json until the output exists.
"@
}
