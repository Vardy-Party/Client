# Launch VardyParty.Desktop inside WSL (WSLg). Needs the .NET 11 preview SDK
# in WSL and (for playback) libvlc: sudo apt install vlc libvlc-dev
#
# The desktop head must build WITHOUT the android workload. On Linux,
# VardyParty.HomeUi defaults to TargetFrameworks net11.0;net11.0-android, so
# a plain restore/run demands the android workload (NETSDK1147). Same fix as
# the CI build-desktop job: pin HomeUiTargetFrameworks=net11.0 in the
# environment so HomeUi's android target never enters the desktop build
# graph, restore explicitly under that pin, then run --no-restore.
#
# Secrets: committed VardyParty.Desktop/appsettings.json is a template.
# Before build we merge Auth0/API values from .NET user-secrets (same
# UserSecretsId as the MAUI head), then git-restore the template on exit.
#
# Usage:
#   pwsh ./scripts/launch-linux-app.ps1
#
# The Linux body lives in launch-linux-app.sh (LF). stdin is that script
# after stripping CR so a Windows CRLF checkout still runs.

$ErrorActionPreference = 'Stop'

$repoWin = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repoWsl = (& wsl.exe wslpath -a $repoWin).Trim()
if ([string]::IsNullOrWhiteSpace($repoWsl)) {
    throw "Failed to resolve WSL path for $repoWin"
}

Write-Host "Launching Vardy Party Desktop from: $repoWsl"
& wsl.exe --cd $repoWsl bash -c "tr -d '\r' < scripts/launch-linux-app.sh | bash -s"
exit $LASTEXITCODE
