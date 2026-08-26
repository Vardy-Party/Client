@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

REM Requires the .NET 11 preview SDK (11.0.100-preview.7 or later): the MAUI
REM head targets net11.0-android, which runs on CoreCLR (Mono AOT is gone and
REM PublishTrimmed must stay ON - the CoreCLR linker path requires it).
REM
REM Default (no args): one APK for 32-bit ARM TVs (armeabi-v7a) and 64-bit
REM ARM phones (arm64-v8a, e.g. Nokia C12).
REM Store/emulator fat APK: package-android.cmd all (arm, arm64, x64 - .NET 11
REM has no android-x86 CoreCLR runtime pack, x86 emulators are gone).
REM
REM Do not pass a single -r or a semicolon RuntimeIdentifiers list on the
REM command line (INSTALL_FAILED_NO_MATCHING_ABIS / NETSDK1083 / MSB1006).
REM Default sets AndroidArmOnly=true so the csproj selects android-arm+arm64.
REM After the MAUI restore, re-restore Hosting/Presentation as net10.0
REM (NETSDK1005) and build with --no-restore.

for /f "tokens=1 delims=." %%v in ('dotnet --version') do set DOTNET_MAJOR=%%v
if not "%DOTNET_MAJOR%"=="11" (
  echo ERROR: dotnet resolves to SDK %DOTNET_MAJOR%.x but the MAUI head needs the
  echo .NET 11 preview SDK. Install 11.0.100-preview.7 or later and make sure no
  echo global.json pins an older SDK. Current: 
  dotnet --version
  exit /b 1
)

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
  echo Fat APK: android-arm, arm64, x64 + trim
  call :RestoreDomain
  if errorlevel 1 exit /b %ERRORLEVEL%
  dotnet restore .\VardyParty\VardyParty.csproj --ignore-failed-sources -p:TargetFrameworks=net11.0-android
  if errorlevel 1 exit /b %ERRORLEVEL%
  call :RestoreDomain
  if errorlevel 1 exit /b %ERRORLEVEL%
  dotnet build .\VardyParty\VardyParty.csproj -f net11.0-android -c Release --no-restore -p:TargetFrameworks=net11.0-android -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
  if errorlevel 1 exit /b %ERRORLEVEL%
  call :ShowApks
  exit /b %ERRORLEVEL%
)

if not "%~1"=="" (
  echo Unknown argument "%~1". Use no args for TV+phone ARM APK, or: package-android.cmd all
  exit /b 1
)

echo Device APK: armeabi-v7a (TV) + arm64-v8a (phones)
call :RestoreDomain
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet restore .\VardyParty\VardyParty.csproj --ignore-failed-sources -p:TargetFrameworks=net11.0-android -p:AndroidArmOnly=true
if errorlevel 1 exit /b %ERRORLEVEL%
call :RestoreDomain
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet build .\VardyParty\VardyParty.csproj -f net11.0-android -c Release --no-restore -p:TargetFrameworks=net11.0-android -p:AndroidArmOnly=true -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
if errorlevel 1 exit /b %ERRORLEVEL%
call :ShowApks
exit /b %ERRORLEVEL%

:RestoreDomain
dotnet restore .\VardyParty.Hosting\VardyParty.Hosting.csproj --ignore-failed-sources
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet restore .\VardyParty.Presentation\VardyParty.Presentation.csproj --ignore-failed-sources
exit /b %ERRORLEVEL%

:ShowApks
echo.
echo Signed APKs:
dir /s /b "VardyParty\bin\Release\net11.0-android\*Signed.apk" 2>nul
set "CANONICAL=VardyParty\bin\Release\net11.0-android\com.vardyparty-Signed.apk"
if not exist "%CANONICAL%" (
  echo ERROR: expected multi-ABI APK at %CANONICAL%
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\assert-android-apk-abis.ps1" -Apk "%CANONICAL%"
if errorlevel 1 exit /b %ERRORLEVEL%
echo.
echo Install on the TV and the phone with:
echo   adb install -r %CANONICAL%
exit /b 0
