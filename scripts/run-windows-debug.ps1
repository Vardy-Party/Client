# Deploy and launch VardyParty on Windows the same way Visual Studio F5 does:
#  - MSBuild with BuildingInsideVisualStudio=true (Windows-only, refreshes AppX layout)
#  - Register the loose MSIX layout from vs.appxrecipe
#  - Launch the registered debug package
#
# Usage:
#   pwsh ./scripts/run-windows-debug.ps1
#   pwsh ./scripts/run-windows-debug.ps1 -Rebuild
#   pwsh ./scripts/run-windows-debug.ps1 -NoLaunch
#
# CS2012 / DLL locked (VBCSCompiler, Xaml.Markup.Compiler, stale VardyParty.exe):
#   Get-Process VardyParty,VBCSCompiler -EA SilentlyContinue | Stop-Process -Force; Get-CimInstance Win32_Process -Filter "Name LIKE '%Markup.Compiler%'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue }

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$Rebuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repoRoot 'VardyParty\VardyParty.csproj'
$winOut = Join-Path $repoRoot "VardyParty\bin\$Configuration\net11.0-windows10.0.19041.0\win-x64"

# The MAUI head targets net11.0-*: fail fast when dotnet resolves to an older SDK.
$sdkVersion = (& dotnet --version)
if (-not $sdkVersion.StartsWith('11.')) {
    throw "dotnet resolves to SDK $sdkVersion but the MAUI head needs the .NET 11 preview SDK (11.0.100-preview.7 or later)."
}

if (-not (Test-Path $project)) {
    throw "Project not found: $project"
}

$buildTarget = if ($Rebuild) { 'Rebuild' } else { 'Build' }
Write-Host "[$buildTarget] $project (Visual Studio-style Windows deploy)..."

& dotnet restore $project -p:CI=true
& dotnet restore (Join-Path $repoRoot 'VardyParty.Hosting\VardyParty.Hosting.csproj')

# CI=true => Windows-only target graph (same effective output as VS Debug).
& dotnet msbuild $project `
    -t:$buildTarget `
    -p:Configuration=$Configuration `
    -p:TargetFramework=net11.0-windows10.0.19041.0 `
    -p:CI=true `
    -p:GenerateTestArtifacts=true `
    -p:RunGenerateBuildInfo=true

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild $buildTarget failed with exit code $LASTEXITCODE"
}

$recipePath = Join-Path $winOut 'AppX\vs.appxrecipe'
$manifestPath = Join-Path $winOut 'AppxManifest.xml'
$appId = $null

if (Test-Path $recipePath) {
  [xml]$recipe = Get-Content -Path $recipePath
  $ns = New-Object System.Xml.XmlNamespaceManager($recipe.NameTable)
  $ns.AddNamespace('m', 'http://schemas.microsoft.com/developer/msbuild/2003')

  $manifestNode = $recipe.SelectSingleNode('//m:AppXManifest', $ns)
  if ($manifestNode -and $manifestNode.Include) {
    $manifestPath = $manifestNode.Include
  }

  $appIdNode = $recipe.SelectSingleNode('//m:RegisteredUserModeAppID', $ns)
  if ($appIdNode) {
    $appId = [string]$appIdNode.InnerText
  }
}

if (-not (Test-Path $manifestPath)) {
  throw "AppxManifest not found at $manifestPath. Build the Windows target first."
}

$layoutDir = Join-Path $winOut 'AppX'

function Sync-AppXLayoutFromBuildOutput {
    param(
        [string]$OutputRoot,
        [string]$ProjectRoot,
        [string]$BuildConfiguration
    )

    $appX = Join-Path $OutputRoot 'AppX'
    if (-not (Test-Path $appX)) {
        Write-Warning "AppX layout folder not found at $appX"
        return
    }

    Write-Host 'Syncing fresh build output into AppX (loose-register runs from here)...'

    # Copy EVERYTHING the build refreshed, not a fixed extension list: any file
    # under the output root (recursively) that is newer than — or missing from,
    # or a different size than — its AppX copy. Three identical stale-bits crash
    # signatures traced back to the old .dll/.exe/.json/.pri allowlist silently
    # skipping changed satellite files.
    # The AppX folder itself is excluded (it is the destination), and the MSIX
    # layout's own manifest/recipe are never overwritten by root-level copies.
    $outputRootFull = (Get-Item -Path $OutputRoot).FullName
    $appXFull = (Get-Item -Path $appX).FullName
    $neverOverwrite = @('AppxManifest.xml', 'vs.appxrecipe')
    $synced = 0

    Get-ChildItem -Path $OutputRoot -File -Recurse |
        Where-Object { -not $_.FullName.StartsWith($appXFull + [IO.Path]::DirectorySeparatorChar) } |
        ForEach-Object {
            $relative = $_.FullName.Substring($outputRootFull.Length).TrimStart('\', '/')
            if ($neverOverwrite -contains (Split-Path -Leaf $relative)) { return }

            $dest = Join-Path $appXFull $relative
            $destItem = Get-Item -Path $dest -ErrorAction SilentlyContinue
            if (-not $destItem -or
                $_.LastWriteTimeUtc -gt $destItem.LastWriteTimeUtc -or
                $_.Length -ne $destItem.Length) {
                $destDir = Split-Path -Parent $dest
                if (-not (Test-Path $destDir)) {
                    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                }
                Copy-Item -Path $_.FullName -Destination $dest -Force
                $synced++
            }
        }

    Write-Host "Synced $synced changed file(s) from build output into AppX."

    # UI sound WAVs: MauiAsset packing does not put them on disk for loose MSIX.
    $soundSource = Join-Path $ProjectRoot 'Resources\Raw\Sounds'
    if (Test-Path $soundSource) {
        foreach ($root in @($OutputRoot, $appX)) {
            $soundDest = Join-Path $root 'Sounds'
            New-Item -ItemType Directory -Path $soundDest -Force | Out-Null
            Copy-Item -Path (Join-Path $soundSource '*.wav') -Destination $soundDest -Force
        }
    }

    # League logo MauiAssets: AppX is not always refreshed on incremental builds.
    $leagueSource = Join-Path $ProjectRoot 'Resources\Images\Leagues'
    if (Test-Path $leagueSource) {
        foreach ($root in @($OutputRoot, $appX)) {
            $leagueDest = Join-Path $root 'images\leagues'
            New-Item -ItemType Directory -Path $leagueDest -Force | Out-Null
            Copy-Item -Path (Join-Path $leagueSource '*') -Destination $leagueDest -Recurse -Force
        }
    }

    # Loose-register uses win-x64\AppxManifest.xml; splash/tile PNGs live under AppX after MSIX layout.
    $msixAssetBases = @(
        'vardyparty_splash_generatedSplashScreen',
        'vardyparty_splashStoreLogo',
        'vardyparty_splashMediumTile',
        'vardyparty_splashLogo',
        'vardyparty_splashSmallTile',
        'vardyparty_splashWideTile',
        'vardyparty_splashLargeTile'
    )
    foreach ($base in $msixAssetBases) {
        $scaled = Join-Path $appX "$base.scale-100.png"
        if (-not (Test-Path $scaled)) { continue }
        Copy-Item -Path $scaled -Destination (Join-Path $appX "$base.png") -Force
    }
    Get-ChildItem -Path $appX -File -Filter '*.png' | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination (Join-Path $OutputRoot $_.Name) -Force
    }
    $appxPri = Join-Path $appX 'resources.pri'
    if (Test-Path $appxPri) {
        Copy-Item -Path $appxPri -Destination (Join-Path $OutputRoot 'resources.pri') -Force
    }
}

function Test-AppXBinaryFresh {
    param([string]$OutputRoot)

    # Hash-compare the app binaries between build output and the AppX layout.
    # Timestamps/sizes proved insufficient (three identical crash signatures
    # against stale registered bits): only identical content is acceptable —
    # the loose-registered package runs from AppX, so a mismatch means the app
    # about to launch is NOT the code that was just built. Fail loudly.
    foreach ($name in @('VardyParty.exe', 'VardyParty.dll')) {
        $rootFile = Join-Path $OutputRoot $name
        $appxFile = Join-Path $OutputRoot (Join-Path 'AppX' $name)

        if (-not (Test-Path $rootFile)) {
            if ($name -eq 'VardyParty.exe') {
                Write-Warning "Build output has no $name — cannot verify AppX freshness for it."
            }
            continue
        }

        if (-not (Test-Path $appxFile)) {
            throw "AppX\$name is missing while the build output has one. The AppX layout is incomplete — rerun with -Rebuild."
        }

        $rootInfo = Get-Item $rootFile
        $appxInfo = Get-Item $appxFile
        $rootHash = (Get-FileHash -Path $rootFile -Algorithm SHA256).Hash
        $appxHash = (Get-FileHash -Path $appxFile -Algorithm SHA256).Hash
        Write-Host "$name build output: $($rootInfo.LastWriteTime) $($rootInfo.Length) bytes sha256=$($rootHash.Substring(0,12))..."
        Write-Host "$name AppX package: $($appxInfo.LastWriteTime) $($appxInfo.Length) bytes sha256=$($appxHash.Substring(0,12))..."

        if ($rootHash -ne $appxHash) {
            throw "AppX\$name is STALE: content hash differs from the freshly built win-x64\$name. The registered package would run old bits. Rerun with -Rebuild (and check nothing holds the AppX files locked)."
        }
    }
}

$projectRoot = Join-Path $repoRoot 'VardyParty'
Sync-AppXLayoutFromBuildOutput -OutputRoot $winOut -ProjectRoot $projectRoot -BuildConfiguration $Configuration
Test-AppXBinaryFresh -OutputRoot $winOut

$leagueDir = Join-Path $layoutDir 'images\leagues'
if (-not (Test-Path (Join-Path $leagueDir 'lebanese-premier-league.png'))) {
  Write-Warning "League logos look stale in $leagueDir. Try -Rebuild."
}

Write-Host "Registering loose package from:"
Write-Host "  $manifestPath"

# Only stop the MAUI app (VardyParty.exe). Never stop VardyParty.LocalService — LAN play depends on it.
$running = Get-Process -Name 'VardyParty' -ErrorAction SilentlyContinue
if ($running) {
  Write-Host 'Stopping running VardyParty instance(s)...'
  $running | Stop-Process -Force
  Start-Sleep -Seconds 1
}

Add-AppxPackage -Register -Path $manifestPath -ForceUpdateFromAnyVersion

$pkg = Get-AppxPackage -Name 'com.vardyparty' -ErrorAction SilentlyContinue
if ($pkg) {
  Write-Host "Registered: $($pkg.PackageFullName)"
  Write-Host "Install location: $($pkg.InstallLocation)"
}

if ($NoLaunch) {
  Write-Host 'Build + register complete (-NoLaunch).'
  return
}

if (-not $appId -and $pkg) {
  $appId = "$($pkg.PackageFamilyName)!App"
}

if (-not $appId) {
  throw 'Could not resolve application id to launch.'
}

function Wait-ForVardyPartyProcess {
    param([int]$TimeoutSeconds = 45)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Name 'VardyParty' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($process) {
            return $process
        }

        Start-Sleep -Milliseconds 250
    }

    return $null
}

function Show-VardyPartyWindow {
    param(
        [int]$ProcessId,
        [int]$TimeoutSeconds = 60
    )

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class VardyPartyWin32 {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static IntPtr FindLargestWindow(int processId) {
        IntPtr best = IntPtr.Zero;
        var bestArea = 0;
        EnumWindows((hWnd, _) => {
            GetWindowThreadProcessId(hWnd, out var windowPid);
            if (windowPid != (uint)processId) {
                return true;
            }

            GetWindowRect(hWnd, out var rect);
            var area = Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
            if (area > bestArea) {
                bestArea = area;
                best = hWnd;
            }

            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@ -ErrorAction SilentlyContinue

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $hwnd = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
        $hwnd = [VardyPartyWin32]::FindLargestWindow($ProcessId)
        if ($hwnd -ne [IntPtr]::Zero) {
            break
        }

        Start-Sleep -Milliseconds 500
    }

    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Warning "VardyParty started but no top-level window handle appeared within ${TimeoutSeconds}s."
        return $false
    }

    [void][VardyPartyWin32]::ShowWindow($hwnd, 9) # SW_RESTORE
    [void][VardyPartyWin32]::ShowWindow($hwnd, 5) # SW_SHOW
    [void][VardyPartyWin32]::BringWindowToTop($hwnd)
    [void][VardyPartyWin32]::SetForegroundWindow($hwnd)
    return [VardyPartyWin32]::IsWindowVisible($hwnd)
}

Write-Host "Launching shell:AppsFolder\$appId"
Start-Process "explorer.exe" "shell:AppsFolder\$appId"

$launched = Wait-ForVardyPartyProcess
if (-not $launched) {
    throw 'VardyParty did not start within 45 seconds.'
}

if (-not (Show-VardyPartyWindow -ProcessId $launched.Id)) {
    Write-Warning 'VardyParty is running but its window is still hidden. Check other desktops/monitors or close and retry.'
}
else {
    Write-Host "VardyParty window foregrounded (pid $($launched.Id))."
}
