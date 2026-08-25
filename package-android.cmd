@echo off
setlocal
cd /d "%~dp0"

REM Local device install: one ABI, no AOT/trim (same idea as ci.yml android compile).
REM Store/CI fat APK: package-android.cmd all
REM
REM -r android-arm64 is required on the MAUI project so the APK contains
REM arm64-v8a native libs (INSTALL_FAILED_NO_MATCHING_ABIS without them).
REM After that restore, re-restore Hosting/Presentation as plain net10.0 so
REM -p:TargetFrameworks=net10.0-android does not poison domain assets (NETSDK1005).
REM Build uses --no-restore so those domain assets stay net10.0.

if exist "VardyParty.Core\VardyParty.Core.csproj" (
  echo VardyParty.Core was removed. Delete the leftover project before packaging:
  echo   rmdir /s /q VardyParty.Core
  echo Then:
  echo   dotnet restore .\VardyParty.Hosting\VardyParty.Hosting.csproj
  echo   .\package-android.cmd
  exit /b 1
)
if exist "VardyParty.Core\" (
  echo Removing leftover VardyParty.Core output folder
  rmdir /s /q "VardyParty.Core"
)

if /I "%~1"=="all" (
  echo Fat APK: android-arm, arm64, x86, x64 + AOT + trim
  call :RestoreDomain
  if errorlevel 1 exit /b %ERRORLEVEL%
  dotnet restore .\VardyParty\VardyParty.csproj --ignore-failed-sources -p:TargetFrameworks=net10.0-android
  if errorlevel 1 exit /b %ERRORLEVEL%
  call :RestoreDomain
  if errorlevel 1 exit /b %ERRORLEVEL%
  dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release --no-restore -p:TargetFrameworks=net10.0-android -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
  exit /b %ERRORLEVEL%
)

echo Device APK: android-arm64, AOT/trim off
call :RestoreDomain
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet restore .\VardyParty\VardyParty.csproj --ignore-failed-sources -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=android-arm64 -r android-arm64
if errorlevel 1 exit /b %ERRORLEVEL%
call :RestoreDomain
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release --no-restore -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=android-arm64 -r android-arm64 -p:RunAotCompilation=false -p:PublishTrimmed=false -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
exit /b %ERRORLEVEL%

:RestoreDomain
dotnet restore .\VardyParty.Hosting\VardyParty.Hosting.csproj --ignore-failed-sources
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet restore .\VardyParty.Presentation\VardyParty.Presentation.csproj --ignore-failed-sources
exit /b %ERRORLEVEL%
