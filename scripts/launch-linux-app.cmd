@echo off
setlocal

set "REPO_WIN=%~dp0.."
for %%I in ("%REPO_WIN%") do set "REPO_WIN=%%~fI"

for /f "delims=" %%I in ('wsl wslpath -a "%REPO_WIN%"') do set "REPO_WSL=%%I"
if not defined REPO_WSL (
  echo Failed to resolve WSL path for %REPO_WIN%
  pause
  exit /b 1
)

REM Launches the MAUI-Avalonia desktop head (VardyParty.Desktop). Needs the
REM .NET 11 preview SDK inside WSL and (for playback) libvlc: sudo apt install vlc libvlc-dev
REM
REM The desktop head must build WITHOUT the android workload. On Linux,
REM VardyParty.HomeUi defaults to TargetFrameworks net11.0;net11.0-android, so
REM a plain restore/run demands the android workload (NETSDK1147). Same fix as
REM the CI build-desktop job: pin HomeUiTargetFrameworks=net11.0 in the
REM environment so HomeUi's android target never enters the desktop build
REM graph, restore explicitly under that pin, then run --no-restore. Restore
REM and build run under the SAME pin, so HomeUi's project.assets.json cannot
REM end up mismatched with what the build consumes (the NETSDK1005 trap the
REM android/iOS re-restore dance in package-android.ps1 / ci.yml guards
REM against). The pin is process-scoped: the next android packaging run
REM re-restores with its own TFMs as before.
REM
REM If the build still fails with NETSDK1147 (e.g. a stale obj/ restored
REM without the pin), the fallback remedy is printed below.
echo Launching Vardy Party Desktop from: %REPO_WSL%
wsl.exe --cd "%REPO_WSL%" bash -lc "set -o pipefail; export DISPLAY=${DISPLAY:-:0}; export WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-wayland-0}; export XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-/mnt/wslg/runtime-dir}; export HomeUiTargetFrameworks=net11.0; LOG=$(mktemp /tmp/vardyparty-desktop-launch.XXXXXX.log); { $HOME/.dotnet/dotnet restore VardyParty.Desktop/VardyParty.Desktop.csproj --ignore-failed-sources && $HOME/.dotnet/dotnet run --project VardyParty.Desktop/VardyParty.Desktop.csproj -c Release --no-restore; } 2>&1 | tee $LOG; RC=$?; if [ $RC -ne 0 ] && grep -q NETSDK1147 $LOG; then echo; echo 'NETSDK1147: the android workload leaked into the desktop build graph.'; echo 'Remedy: dotnet workload install android'; fi; rm -f $LOG; exit $RC"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo Vardy Party Desktop exited with code %EXITCODE%.
  echo Press any key to close this window.
  pause >nul
)

exit /b %EXITCODE%
