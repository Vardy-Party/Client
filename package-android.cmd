@echo off
setlocal
cd /d "%~dp0"

REM Local device install: one ABI, no AOT/trim (same idea as ci.yml android compile).
REM Store/CI fat APK: package-android.cmd all
REM
REM Restore Hosting first (plain net10.0), then the MAUI app. Do not restore a leftover
REM VardyParty.Core folder — that project was deleted in the domain-assembly split.

if exist "VardyParty.Core\VardyParty.Core.csproj" (
  echo VardyParty.Core was removed. Delete the leftover project before packaging:
  echo   rmdir /s /q VardyParty.Core
  echo Then restore:
  echo   dotnet restore .\VardyParty.Hosting\VardyParty.Hosting.csproj
  echo   dotnet restore .\VardyParty\VardyParty.csproj -f net10.0-android
  exit /b 1
)
if exist "VardyParty.Core\" (
  echo Removing leftover VardyParty.Core output folder
  rmdir /s /q "VardyParty.Core"
)

if /I "%~1"=="all" (
  echo Fat APK: android-arm, arm64, x86, x64 + AOT + trim
  dotnet restore .\VardyParty.Hosting\VardyParty.Hosting.csproj --ignore-failed-sources
  if errorlevel 1 exit /b %ERRORLEVEL%
  dotnet restore .\VardyParty\VardyParty.csproj --ignore-failed-sources -p:TargetFrameworks=net10.0-android
  if errorlevel 1 exit /b %ERRORLEVEL%
  dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release -p:TargetFrameworks=net10.0-android -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
  exit /b %ERRORLEVEL%
)

echo Device APK: android-arm64, AOT/trim off
dotnet restore .\VardyParty.Hosting\VardyParty.Hosting.csproj --ignore-failed-sources
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet restore .\VardyParty\VardyParty.csproj --ignore-failed-sources -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=android-arm64 -r android-arm64
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=android-arm64 -r android-arm64 -p:RunAotCompilation=false -p:PublishTrimmed=false -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
exit /b %ERRORLEVEL%
