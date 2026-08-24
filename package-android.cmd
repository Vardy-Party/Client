@echo off
setlocal
cd /d "%~dp0"

REM Local device install: one ABI, no AOT/trim (same idea as ci.yml android compile).
REM Store/CI fat APK: package-android.cmd all
if /I "%~1"=="all" (
  echo Fat APK: android-arm, arm64, x86, x64 + AOT + trim
  dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release -p:TargetFrameworks=net10.0-android -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
  exit /b %ERRORLEVEL%
)

echo Device APK: android-arm64, AOT/trim off
dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers=android-arm64 -r android-arm64 -p:RunAotCompilation=false -p:PublishTrimmed=false -p:RunGenerateBuildInfo=true -p:RunGenerateSplash=true -p:AndroidKeyStore=false -p:PatchAppSettings=true
exit /b %ERRORLEVEL%
