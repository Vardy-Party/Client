# Launch VardyParty.Linux inside WSL (WSLg). Needs the .NET 11 preview SDK
# in WSL and (for playback) libvlc: sudo apt install vlc libvlc-dev
#
# The Linux head must build WITHOUT the android workload. On Linux,
# VardyParty.HomeUi defaults to TargetFrameworks net11.0;net11.0-android, so
# a plain restore/run demands the android workload (NETSDK1147). Same fix as
# the CI build-linux job: pin HomeUiTargetFrameworks=net11.0 in the
# environment so HomeUi's android target never enters the Linux build
# graph, restore explicitly under that pin, then run --no-restore.
#
# Secrets: committed VardyParty.Linux/appsettings.json is a template.
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
# Native argv to wsl.exe eats `\` (wslpath then sees C:Usersjonbr...).
# Forward slashes are valid Windows paths and survive the hop.
$repoPosix = $repoWin.Replace('\', '/')
$repoWsl = (& wsl.exe wslpath -a $repoPosix | Select-Object -First 1)
if ($repoWsl) { $repoWsl = $repoWsl.ToString().Trim() }
if ([string]::IsNullOrWhiteSpace($repoWsl)) {
    throw "Failed to resolve WSL path for $repoWin"
}

Write-Host "Launching Vardy Party Linux from: $repoWsl"
& wsl.exe --cd $repoWsl bash -c "tr -d '\r' < scripts/launch-linux-app.sh | bash -s"
exit $LASTEXITCODE
