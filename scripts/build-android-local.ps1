#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds an Android APK locally on Windows while Visual Studio is open.

.DESCRIPTION
    Uses 'dotnet build -f net10.0-android' which produces an APK directly for
    Release configuration. The -f flag bypasses the csproj's OS-conditional
    TargetFrameworks, and the csproj's RuntimeIdentifiers condition automatically
    includes all four Android ABIs for a universal APK.

    Run from a standalone pwsh terminal (not VS integrated terminal) to avoid
    timeout issues during AOT compilation (~10-20 min for universal).

.PARAMETER Configuration
    Build configuration (default: Release).

.PARAMETER SkipClean
    Skip cleaning obj/bin directories.

.PARAMETER SignApk
    Enable keystore signing (requires ANDROID_SIGNING_KEY_PASS / ANDROID_SIGNING_STORE_PASS env vars).

.PARAMETER Rid
    Single Android RID for a faster single-arch build (e.g. 'android-arm64').
    Default: omitted (universal APK with all ABIs from csproj).

.EXAMPLE
    .\scripts\build-android-local.ps1
    .\scripts\build-android-local.ps1 -Rid android-arm64
    .\scripts\build-android-local.ps1 -SkipClean -SignApk
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipClean,
    [switch]$SignApk,
    [string]$Rid = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$vpProject = Join-Path $repoRoot "VardyParty\VardyParty.csproj"

$apkType = if ($Rid) { $Rid } else { "Universal" }
Write-Host "`n=== VardyParty Android Local Build ===" -ForegroundColor Cyan
Write-Host "APK: $apkType | Config: $Configuration" -ForegroundColor DarkCyan

# ---------- Step 1: Shut down build servers ----------
Write-Host "`n[1/3] Shutting down build servers..." -ForegroundColor Yellow
dotnet build-server shutdown 2>$null

# ---------- Step 2: Clean ----------
if (-not $SkipClean) {
    Write-Host "[2/3] Cleaning VardyParty build artifacts..." -ForegroundColor Yellow
    # Only clean VardyParty — NOT Core. Core targets net10.0 and its existing
    # obj/ from VS is valid for Android builds. Cleaning it triggers VS to
    # re-restore, which races with our build.
    @(
        (Join-Path $repoRoot "VardyParty\obj"),
        (Join-Path $repoRoot "VardyParty\bin")
    ) | ForEach-Object {
        if (Test-Path $_) { Remove-Item $_ -Recurse -Force -ErrorAction SilentlyContinue }
    }
} else {
    Write-Host "[2/3] Skipping clean..." -ForegroundColor DarkGray
}

# ---------- Step 3: Build Android APK ----------
Write-Host "[3/3] Building Android APK ($apkType)..." -ForegroundColor Yellow

# Single dotnet build call — matches the working command from before.
# -f net10.0-android sets TargetFramework (singular) for VardyParty only.
# DO NOT pass -p:TargetFrameworks — that's a global property that propagates
# to Core and corrupts its restore (net10.0-android instead of net10.0).
# The csproj's RuntimeIdentifiers condition adds all 4 ABIs automatically.
$keyStoreFlag = if ($SignApk) { "true" } else { "false" }
$buildArgs = @(
    "build", $vpProject,
    "-f", "net10.0-android",
    "-c", $Configuration,
    "-p:UseMonoRuntime=true",
    "-p:RunGenerateBuildInfo=true",
    "-p:RunGenerateSplash=true",
    "-p:AndroidKeyStore=$keyStoreFlag",
    "-p:PatchAppSettings=true"
)
if ($Rid) {
    $buildArgs += @("-r", $Rid)
}

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "Android build failed" }

# ---------- Report output ----------
Write-Host "`nLocating APK..." -ForegroundColor Yellow
$searchPaths = @(
    (Join-Path $repoRoot "VardyParty\bin\$Configuration\net10.0-android\publish"),
    (Join-Path $repoRoot "VardyParty\bin\$Configuration\net10.0-android")
)
$apk = $null
foreach ($sp in $searchPaths) {
    if (Test-Path $sp) {
        $apk = Get-ChildItem $sp -Filter "*-Signed.apk" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $apk) {
            $apk = Get-ChildItem $sp -Filter "*.apk" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        if ($apk) { break }
    }
}

if ($apk) {
    Write-Host "`n=== BUILD SUCCEEDED ===" -ForegroundColor Green
    Write-Host "APK:  $($apk.FullName)" -ForegroundColor Green
    Write-Host "Size: $([math]::Round($apk.Length / 1MB, 1)) MB" -ForegroundColor Green
} else {
    Write-Host "`n=== BUILD COMPLETED ===" -ForegroundColor Green
    Write-Host "Check VardyParty\bin\$Configuration\net10.0-android\ for output" -ForegroundColor Yellow
}
