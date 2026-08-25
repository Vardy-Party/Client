@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

REM Local device install: ARM 32+64, no AOT/trim (Android TV).
REM Store/CI fat APK: package-android.cmd all
REM
REM Do not pass a single -r. That produces an arm64-only APK in
REM android-arm64\ while adb install of the leftover root APK fails with
REM INSTALL_FAILED_NO_MATCHING_ABIS on 32-bit TVs (and stale no-ABI APKs).
REM After the MAUI restore, re-restore Hosting/Presentation as net10.0
REM (NETSDK1005) and build with --no-restore.

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
  if errorlevel 1 exit /b %ERRORLEVEL%
  call :ShowApks
  exit /b 0
)

echo Device APK: android-arm + android-arm64 (TV), AOT/trim off
call :RestoreDomain
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet restore .\VardyParty\VardyParty.csproj --ignore-failed-sources -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=android-arm;android-arm64
if errorlevel 1 exit /b %ERRORLEVEL%
call :RestoreDomain
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release --no-restore -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=android-arm;android-arm64 -p:RunAotCompilation=false -p:PublishTrimmed=false -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
if errorlevel 1 exit /b %ERRORLEVEL%
call :ShowApks
exit /b 0

:RestoreDomain
dotnet restore .\VardyParty.Hosting\VardyParty.Hosting.csproj --ignore-failed-sources
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet restore .\VardyParty.Presentation\VardyParty.Presentation.csproj --ignore-failed-sources
exit /b %ERRORLEVEL%

:ShowApks
echo.
echo Signed APKs:
dir /s /b "VardyParty\bin\Release\net10.0-android\*Signed.apk" 2>nul
set "RID_APK=VardyParty\bin\Release\net10.0-android\android-arm64\com.vardyparty-Signed.apk"
set "CANONICAL=VardyParty\bin\Release\net10.0-android\com.vardyparty-Signed.apk"
if exist "%RID_APK%" (
  copy /Y "%RID_APK%" "%CANONICAL%" >nul
  echo Copied android-arm64 APK to %CANONICAL%
)
echo.
echo Install with:
echo   adb install -r %CANONICAL%
echo If that still fails, check the TV ABI:
echo   adb shell getprop ro.product.cpu.abi
exit /b 0
