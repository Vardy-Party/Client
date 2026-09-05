# Local Windows packaging for the MAUI head (net11.0-windows10.0.19041.0).
# Requires the .NET 11 preview SDK (11.0.100-preview.7 or later).
#
# Usage:
#   pwsh ./package-windows.ps1
#   pwsh ./package-windows.ps1 -KeepPatchedAppSettings
#
# The build embeds user-secrets into MauiAsset/EmbeddedResource by patching
# SOURCE VardyParty/appsettings.json before CoreCompile
# (-p:PatchAppSettings=true -> PatchAppSettingsForLocalAndroid, which also
# covers the Windows TFM). Same contract as package-android.ps1: this script
# does NOT git-restore afterward (mid-build restore races with compile).
# Restore yourself before committing:
#   git restore VardyParty/appsettings.json

param(
    # After a successful build, leave secrets in source appsettings.json.
    # Default is also leave them (no auto git restore). Kept for compat.
    [switch]$KeepPatchedAppSettings
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Fail fast when dotnet resolves to an older SDK (a global.json pin, for example).
$sdkVersion = (& dotnet --version)
if (-not $sdkVersion.StartsWith('11.')) {
    throw "dotnet resolves to SDK $sdkVersion but the MAUI head needs the .NET 11 preview SDK (11.0.100-preview.7 or later). Make sure no global.json pins an older SDK."
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

if ($KeepPatchedAppSettings) {
    Write-Host ''
    Write-Host '-KeepPatchedAppSettings: source appsettings.json was patched and is left dirty (default now). Restore with: git restore VardyParty/appsettings.json'
} else {
    Write-Host ''
    Write-Host 'NOTE: VardyParty/appsettings.json may contain local secrets after this build. Restore before committing:'
    Write-Host '  git restore VardyParty/appsettings.json'
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
Ensure user-secrets are set, then re-run: pwsh ./package-windows.ps1
Do not git restore VardyParty/appsettings.json until publish finishes.
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
