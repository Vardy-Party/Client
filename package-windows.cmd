@echo off
REM Requires the .NET 11 preview SDK (the MAUI head targets net11.0-windows).
for /f "tokens=1 delims=." %%v in ('dotnet --version') do set DOTNET_MAJOR=%%v
if not "%DOTNET_MAJOR%"=="11" (
  echo ERROR: dotnet resolves to SDK %DOTNET_MAJOR%.x but the MAUI head needs the
  echo .NET 11 preview SDK. Install 11.0.100-preview.7 or later.
  exit /b 1
)
dotnet publish .\VardyParty\VardyParty.csproj -f net11.0-windows10.0.19041.0 -c:Release -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true
