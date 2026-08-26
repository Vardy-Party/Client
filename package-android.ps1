# Local Android packaging for the MAUI head (net11.0-android, CoreCLR).
# Requires the .NET 11 preview SDK (11.0.100-preview.7 or later): Mono AOT is
# gone and PublishTrimmed must stay ON — the CoreCLR linker path requires it.
#
# Usage:
#   pwsh ./package-android.ps1                  # device APK (default)
#   pwsh ./package-android.ps1 -Mode all        # store/emulator fat APK
#   pwsh ./package-android.ps1 -KeepPatchedAppSettings
#
# Default (device): one APK for 32-bit ARM TVs (armeabi-v7a) and 64-bit ARM
# phones (arm64-v8a, e.g. Nokia C12) — AndroidArmOnly=true.
# Fat APK (all): android-arm, arm64 and x64 (.NET 11 has no android-x86
# CoreCLR runtime pack, x86 emulators are gone).
#
# Do not pass a single -r or a semicolon RuntimeIdentifiers list on the
# command line (INSTALL_FAILED_NO_MATCHING_ABIS / NETSDK1083 / MSB1006):
# the csproj selects the RID set from AndroidArmOnly.
#
# The build embeds your user secrets into the APK by patching the SOURCE
# VardyParty/appsettings.json before build (-p:PatchAppSettings=true ->
# PatchAppSettingsForLocalAndroid target -> scripts/patch-appsettings-android.ps1).
# The patched file is git-restored when the script finishes unless you pass
# -KeepPatchedAppSettings.

param(
    [ValidateSet('device', 'all')]
    [string]$Mode = 'device',

    # Keep the locally patched VardyParty/appsettings.json in the working tree
    # (skip the post-build `git restore`), e.g. to inspect what got embedded.
    [switch]$KeepPatchedAppSettings
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Fail fast when dotnet resolves to an older SDK (a global.json pin, for example).
$sdkVersion = (& dotnet --version)
if (-not $sdkVersion.StartsWith('11.')) {
    throw "dotnet resolves to SDK $sdkVersion but the MAUI head needs the .NET 11 preview SDK (11.0.100-preview.7 or later). Make sure no global.json pins an older SDK."
}

if (Test-Path 'VardyParty.Core/VardyParty.Core.csproj') {
    Write-Host 'VardyParty.Core was removed. Delete the leftover project before packaging:'
    Write-Host '  Remove-Item -Recurse -Force VardyParty.Core'
    Write-Host 'Then:'
    Write-Host '  dotnet restore ./VardyParty.Hosting/VardyParty.Hosting.csproj'
    Write-Host '  pwsh ./package-android.ps1'
    exit 1
}
if (Test-Path 'VardyParty.Core') {
    Write-Host 'Removing leftover VardyParty.Core output folder'
    Remove-Item -Recurse -Force 'VardyParty.Core'
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

# The MAUI restore below flows -p:TargetFrameworks=net11.0-android into the
# domain libraries and rewrites their obj/project.assets.json without a plain
# net11.0 target (NETSDK1005 on the next build). Restoring Hosting and
# Presentation — the two roots of the domain graph — before AND after the MAUI
# restore puts the assets back; the Release build then runs --no-restore.
function Restore-DomainGraph {
    Invoke-Dotnet @('restore', './VardyParty.Hosting/VardyParty.Hosting.csproj', '--ignore-failed-sources')
    Invoke-Dotnet @('restore', './VardyParty.Presentation/VardyParty.Presentation.csproj', '--ignore-failed-sources')
}

$modeProps = @()
if ($Mode -eq 'all') {
    Write-Host 'Fat APK: android-arm, arm64, x64 + trim'
}
else {
    Write-Host 'Device APK: armeabi-v7a (TV) + arm64-v8a (phones)'
    $modeProps = @('-p:AndroidArmOnly=true')
}

Restore-DomainGraph
Invoke-Dotnet (@('restore', './VardyParty/VardyParty.csproj', '--ignore-failed-sources', '-p:TargetFrameworks=net11.0-android') + $modeProps)
Restore-DomainGraph

# -m:1: trimming two/three RIDs on parallel MSBuild nodes has crashed ILLink
# from memory pressure on real Windows boxes; one node halves peak memory.
$buildArgs = @(
    'build', './VardyParty/VardyParty.csproj',
    '-f', 'net11.0-android',
    '-c', 'Release',
    '--no-restore',
    '-m:1',
    '-p:TargetFrameworks=net11.0-android'
) + $modeProps + @(
    '-p:RunGenerateBuildInfo=true',
    '-p:RunGenerateSplash=true',
    '-p:AndroidKeyStore=false',
    '-p:PatchAppSettings=true'
)

$buildFailed = $false
$buildLog = New-Object System.Collections.Generic.List[string]
try {
    # Relax EAP around the merged-stream pipeline: Windows PowerShell 5.1 turns
    # native stderr lines into ErrorRecords, which would abort under 'Stop'.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & dotnet @buildArgs 2>&1 | ForEach-Object {
            $line = "$_"
            $buildLog.Add($line)
            Write-Host $line
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($LASTEXITCODE -ne 0) { $buildFailed = $true }
}
finally {
    if ($KeepPatchedAppSettings) {
        Write-Host ''
        Write-Host '-KeepPatchedAppSettings: leaving the patched VardyParty/appsettings.json in the working tree.'
        Write-Host 'Revert it later with: git restore VardyParty/appsettings.json'
    }
    else {
        Write-Host ''
        Write-Host 'Reverting the secrets-patched VardyParty/appsettings.json (git restore); pass -KeepPatchedAppSettings to skip.'
        & git restore 'VardyParty/appsettings.json'
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'git restore VardyParty/appsettings.json failed — revert it manually before committing.'
        }
    }
}

if ($buildFailed) {
    if (($buildLog -join "`n") -match 'XA5207') {
        Write-Host ''
        Write-Warning 'XA5207: the Android SDK is missing the API level this build needs.'
        Write-Host 'Install it with the MAUI helper target:'
        Write-Host '  dotnet build ./VardyParty/VardyParty.csproj -f net11.0-android -t:InstallAndroidDependencies -p:AcceptAndroidSDKLicenses=True'
        Write-Host 'Run that from an ELEVATED prompt if your Android SDK lives under Program Files.'
    }
    Write-Warning 'If the failure was ILLink/trimming dying or the machine running out of memory: keep -m:1 (this script always passes it) and make sure Windows has a page file (system-managed or generously sized) — parallel trimming with no page file is what crashed local packaging before.'
    throw 'Android package build failed.'
}

Write-Host ''
Write-Host 'Signed APKs:'
Get-ChildItem -Path 'VardyParty/bin/Release/net11.0-android' -Recurse -Filter '*Signed.apk' -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  $($_.FullName)" }

$canonical = 'VardyParty/bin/Release/net11.0-android/com.vardyparty-Signed.apk'
if (-not (Test-Path $canonical)) {
    throw "Expected multi-ABI APK at $canonical"
}

& "$PSScriptRoot/scripts/assert-android-apk-abis.ps1" -Apk $canonical

Write-Host ''
Write-Host 'Install on the TV and the phone with:'
Write-Host "  adb install -r $canonical"
