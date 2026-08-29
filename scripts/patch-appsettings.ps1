<#
.SYNOPSIS
  Merge Auth0 / Api values from .NET user-secrets into an appsettings.json template.

.DESCRIPTION
  Used by local Android packaging (-p:PatchAppSettings=true) and Desktop/Linux
  local builds. Resolves the user-secrets store on Windows (%APPDATA%) and on
  Linux/macOS (~/.microsoft/usersecrets).

.PARAMETER AppSettingsPath
  Path to the template appsettings.json to patch in place.

.PARAMETER UserSecretsId
  The project's UserSecretsId (GUID folder under the user-secrets root).
#>
param(
    [Parameter(Mandatory = $true)][string]$AppSettingsPath,
    [Parameter(Mandatory = $true)][string]$UserSecretsId
)

$ErrorActionPreference = 'Stop'

function Resolve-UserSecretsPath {
    param([string]$Id)

    $candidates = @()
    if ($env:APPDATA) {
        $candidates += (Join-Path $env:APPDATA "Microsoft\UserSecrets\$Id\secrets.json")
    }
    if ($env:HOME) {
        $candidates += (Join-Path $env:HOME ".microsoft/usersecrets/$Id/secrets.json")
    }
    if ($IsLinux -or $IsMacOS) {
        $candidates += (Join-Path $HOME ".microsoft/usersecrets/$Id/secrets.json")
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) {
            return $c
        }
    }
    return $null
}

Write-Host "[BUILD] Patching appsettings from user-secrets..." -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $AppSettingsPath)) {
    throw "appsettings not found: $AppSettingsPath"
}

$secretsPath = Resolve-UserSecretsPath -Id $UserSecretsId
if (-not $secretsPath) {
    throw "User secrets not found for id $UserSecretsId (looked under APPDATA and ~/.microsoft/usersecrets)."
}

$appsettings = Get-Content -LiteralPath $AppSettingsPath -Raw -Encoding utf8 | ConvertFrom-Json
$secrets = Get-Content -LiteralPath $secretsPath -Raw -Encoding utf8 | ConvertFrom-Json

if (-not $appsettings.PSObject.Properties['Auth0']) {
    $appsettings | Add-Member -NotePropertyName Auth0 -NotePropertyValue ([PSCustomObject]@{})
}
if (-not $appsettings.PSObject.Properties['Api']) {
    $appsettings | Add-Member -NotePropertyName Api -NotePropertyValue ([PSCustomObject]@{})
}

foreach ($p in $secrets.PSObject.Properties) {
    if ($p.Name -match '^Auth0:(.+)$') {
        $appsettings.Auth0 | Add-Member -NotePropertyName $Matches[1] -NotePropertyValue $p.Value -Force
    }
    elseif ($p.Name -match '^Api:(.+)$') {
        $appsettings.Api | Add-Member -NotePropertyName $Matches[1] -NotePropertyValue $p.Value -Force
    }
}

if ($appsettings.Auth0.PSObject.Properties['TokenLeewaySeconds']) {
    $appsettings.Auth0.TokenLeewaySeconds = [int]$appsettings.Auth0.TokenLeewaySeconds
}
if ($appsettings.PSObject.Properties['AllowUserSecrets']) {
    $appsettings.PSObject.Properties.Remove('AllowUserSecrets')
}

$json = $appsettings | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($AppSettingsPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
Write-Host "[BUILD] Patched $AppSettingsPath from $secretsPath" -ForegroundColor Green
