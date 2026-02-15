param(
    [switch]$Force
)

if (-not $Force) {
    Write-Host "This script will stop common build processes and attempt to delete the NuGet global packages folder and HTTP cache."
    Write-Host "Run with -Force to proceed (requires admin privileges for some removals)."
    Write-Host "Example: pwsh -NoProfile -ExecutionPolicy Bypass ./scripts/clear-nuget-cache-forced.ps1 -Force"
    exit 1
}

# Stop common processes that may hold locks
$procs = @('devenv','dotnet','msbuild','MSBuild','VBCSCompiler','Microsoft.WebTools.Razor','Microsoft.VisualStudio.Web.CodeGeneration')
foreach ($p in $procs) {
    Get-Process -Name $p -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue; Write-Host "Stopped process: $($_.Name) ($($_.Id))" } catch { }
    }
}

# Helper to remove a path with retries
function Remove-PathWithRetries([string]$path) {
    if (-not (Test-Path $path)) { return }
    for ($i=0; $i -lt 6; $i++) {
        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            Write-Host "Deleted: $path"
            return
        }
        catch {
            Write-Warning "Attempt $($i+1): Failed to delete $path - $($_.Exception.Message)"
            Start-Sleep -Seconds (2 + $i)
        }
    }
    Write-Error "Unable to delete $path. Consider closing programs or rebooting and try again as administrator."
}

# Delete NuGet global packages and HTTP cache
$nugetPackages = Join-Path $env:USERPROFILE ".nuget\packages"
$nugetHttpCache = Join-Path $env:LOCALAPPDATA "NuGet\v3-cache"

Write-Host "Attempting to clear: $nugetPackages"
Remove-PathWithRetries -path $nugetPackages

Write-Host "Attempting to clear: $nugetHttpCache"
Remove-PathWithRetries -path $nugetHttpCache

# Clear NuGet temp cache
$nugetTemp = Join-Path $env:LOCALAPPDATA "Temp\NuGetScratch"
Write-Host "Attempting to clear: $nugetTemp"
Remove-PathWithRetries -path $nugetTemp

# Attempt dotnet nuget locals clear
try {
    dotnet nuget locals all --clear
} catch {
    Write-Warning "dotnet nuget locals failed: $($_.Exception.Message)"
}

Write-Host "If files remain locked, rebooting Windows will usually clear handles held by background services. Run this script as Administrator for best results."