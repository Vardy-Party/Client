# Local Windows packaging for the MAUI head (net11.0-windows10.0.19041.0).
# Requires the .NET 11 preview SDK (11.0.100-preview.7 or later).
#
# Usage:
#   pwsh ./package-windows.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Fail fast when dotnet resolves to an older SDK (a global.json pin, for example).
$sdkVersion = (& dotnet --version)
if (-not $sdkVersion.StartsWith('11.')) {
    throw "dotnet resolves to SDK $sdkVersion but the MAUI head needs the .NET 11 preview SDK (11.0.100-preview.7 or later). Make sure no global.json pins an older SDK."
}

& dotnet publish ./VardyParty/VardyParty.csproj -f net11.0-windows10.0.19041.0 -c Release -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}
