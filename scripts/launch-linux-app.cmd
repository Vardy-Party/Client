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

echo Launching VardyParty Linux from: %REPO_WSL%
wsl.exe --cd "%REPO_WSL%" bash -lc "export DISPLAY=${DISPLAY:-:0}; export WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-wayland-0}; export XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-/mnt/wslg/runtime-dir}; $HOME/.dotnet/dotnet run --project VardyParty.Linux/VardyParty.Linux.csproj -c Release"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo VardyParty Linux exited with code %EXITCODE%.
  echo Press any key to close this window.
  pause >nul
)

exit /b %EXITCODE%
