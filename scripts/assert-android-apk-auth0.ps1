<#
.SYNOPSIS
  Verify a packaged Android APK has non-empty Auth0 ClientId/Domain in assets/appsettings.json.

.DESCRIPTION
  MauiProgram loads Auth0 from the EmbeddedResource, but MauiAsset appsettings.json
  in the APK is a reliable proxy for what the local patch produced. Does not print
  secret values — only EMPTY/NON_EMPTY and fails the build if missing.
#>
param(
    [Parameter(Mandatory = $true)][string]$Apk
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Apk)) {
    throw "APK not found: $Apk"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Apk))
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -eq 'assets/appsettings.json' } | Select-Object -First 1
    if (-not $entry) {
        throw "APK is missing assets/appsettings.json: $Apk"
    }

    $stream = $entry.Open()
    try {
        $reader = New-Object System.IO.StreamReader($stream)
        $json = $reader.ReadToEnd()
        $reader.Close()
    }
    finally {
        $stream.Dispose()
    }
}
finally {
    $zip.Dispose()
}

$appsettings = $json | ConvertFrom-Json
$clientId = [string]$appsettings.Auth0.ClientId
$domain = [string]$appsettings.Auth0.Domain

$clientState = if ([string]::IsNullOrWhiteSpace($clientId)) { 'EMPTY' } else { 'NON_EMPTY' }
$domainState = if ([string]::IsNullOrWhiteSpace($domain)) { 'EMPTY' } else { 'NON_EMPTY' }

Write-Host "[APK CHECK] assets/appsettings.json Auth0.ClientId=$clientState Auth0.Domain=$domainState"

if ($clientState -ne 'NON_EMPTY' -or $domainState -ne 'NON_EMPTY') {
    throw @"
Packaged APK has empty Auth0 ClientId/Domain in assets/appsettings.json.
The patch target likely ran, then appsettings.json was restored to the empty
git template before CoreCompile/MauiAssets (e.g. git restore/checkout mid-build).
Re-run: pwsh ./package-android.ps1
Do not git restore VardyParty/appsettings.json until the APK exists.
"@
}
