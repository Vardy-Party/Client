@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"

REM Default (no args): one APK for 32-bit ARM TVs (armeabi-v7a) and 64-bit
REM ARM phones (arm64-v8a, e.g. Nokia C12). AOT/trim off.
REM Store/emulator fat APK: package-android.cmd all
REM
REM Do not pass a single -r. That produces an arm64-only APK that will not
REM install on armeabi-v7a TVs (INSTALL_FAILED_NO_MATCHING_ABIS).
REM After the MAUI restore, re-restore Hosting/Presentation as net10.0
REM (NETSDK1005) and build with --no-restore.
REM MSBuild splits -p:A=1;B=2 on ";". Encode the RID list as %%3B in this .cmd
REM (batch %% -> %3B for MSBuild). Quotes around the value are not enough.

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
  exit /b %ERRORLEVEL%
)

if not "%~1"=="" (
  echo Unknown argument "%~1". Use no args for TV+phone ARM APK, or: package-android.cmd all
  exit /b 1
)

echo Device APK: armeabi-v7a (TV) + arm64-v8a (phones), AOT/trim off
set "ANDROID_RIDS=android-arm%%3Bandroid-arm64"
call :RestoreDomain
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet restore .\VardyParty\VardyParty.csproj --ignore-failed-sources -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=!ANDROID_RIDS!
if errorlevel 1 exit /b %ERRORLEVEL%
call :RestoreDomain
if errorlevel 1 exit /b %ERRORLEVEL%
dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release --no-restore -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=!ANDROID_RIDS! -p:RunAotCompilation=false -p:PublishTrimmed=false -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
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
dir /s /b "VardyParty\bin\Release\net10.0-android\*Signed.apk" 2>nul
set "CANONICAL=VardyParty\bin\Release\net10.0-android\com.vardyparty-Signed.apk"
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
