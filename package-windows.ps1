# Local Windows packaging for the MAUI head (net11.0-windows10.0.19041.0).
# Requires the .NET 11 RC1 SDK (11.0.100-rc.1 or later).
#
# Usage:
#   pwsh ./package-windows.ps1
#   pwsh ./package-windows.ps1 -KeepPatchedAppSettings
#
# The build embeds user-secrets into MauiAsset/EmbeddedResource by patching a
# copy under IntermediateOutputPath (not the git template) before CoreCompile
# (-p:PatchAppSettings=true -> PatchAppSettingsForLocalAndroid, which also
# covers the Windows TFM). Source appsettings.json is left untouched.

param(
    # Compat no-op: secrets are written under obj/, not the source template.
    [switch]$KeepPatchedAppSettings
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Fail fast when dotnet resolves to an older SDK (a global.json pin, for example).
$sdkVersion = (& dotnet --version)
if (-not $sdkVersion.StartsWith('11.')) {
    throw "dotnet resolves to SDK $sdkVersion but the MAUI head needs the .NET 11 RC1 SDK (11.0.100-rc.1 or later). Make sure no global.json pins an older SDK."
}

$publishArgs = @(
    'publish', './VardyParty/VardyParty.csproj',
    '-f', 'net11.0-windows10.0.19041.0',
    '-c', 'Release',
    '-p:RunGenerateBuildInfo=true',
    '-p:RunGenerateSplash=true',
    '-p:PatchAppSettings=true'
)

$publishFailed = $false
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { $publishFailed = $true }

Write-Host ''
if ($KeepPatchedAppSettings) {
    Write-Host '-KeepPatchedAppSettings: ignored — Auth0/API secrets were patched under obj/ only; source appsettings.json was not modified.'
} else {
    Write-Host 'NOTE: Auth0/API secrets were patched under obj/ only; source VardyParty/appsettings.json was not modified.'
}

if ($publishFailed) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Fail closed if the packaged MauiAsset still has empty Auth0 (same intent as
# assert-android-apk-auth0.ps1; Windows ships appsettings.json next to the DLL).
$winOut = 'VardyParty/bin/Release/net11.0-windows10.0.19041.0/win-x64'
$candidates = @(
    (Join-Path $winOut 'appsettings.json'),
    (Join-Path $winOut 'AppX/appsettings.json')
)
$appsettingsPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $appsettingsPath) {
    throw "Expected packaged appsettings.json under $winOut (or AppX). Publish may have used a different layout."
}

$appsettings = Get-Content -LiteralPath $appsettingsPath -Raw -Encoding utf8 | ConvertFrom-Json
$clientId = [string]$appsettings.Auth0.ClientId
$domain = [string]$appsettings.Auth0.Domain
$clientState = if ([string]::IsNullOrWhiteSpace($clientId)) { 'EMPTY' } else { 'NON_EMPTY' }
$domainState = if ([string]::IsNullOrWhiteSpace($domain)) { 'EMPTY' } else { 'NON_EMPTY' }
Write-Host "[WIN CHECK] $appsettingsPath Auth0.ClientId=$clientState Auth0.Domain=$domainState"
if ($clientState -ne 'NON_EMPTY' -or $domainState -ne 'NON_EMPTY') {
    throw @"
Packaged Windows output has empty Auth0 ClientId/Domain in appsettings.json.
Ensure user-secrets Auth0:ClientId/Domain are set and -p:PatchAppSettings=true, then re-run:
  pwsh ./package-windows.ps1
"@
}

Write-Host ''
Write-Host 'Windows package output:'
Write-Host "  $winOut"
$msixRoot = Join-Path $winOut 'AppPackages'
if (Test-Path -LiteralPath $msixRoot) {
    Get-ChildItem -Path $msixRoot -Filter '*.msix' -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "  $($_.FullName)" }
}
