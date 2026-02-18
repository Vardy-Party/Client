# VardyParty Windows Installation Script
# This script installs the VardyParty MSIX package with certificate trust setup

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = Get-Location }

$CertPath = Join-Path $scriptDir "vardyparty.pfx"
$CerPath = Join-Path $scriptDir "vardyparty.cer"
$MsixPath = Join-Path $scriptDir "VardyParty-windows.msix"

$CertPassword = Read-Host -Prompt "Enter certificate password" -AsSecureString

function Test-AdminRights {
  $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
  return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

Write-Host "VardyParty Installer"
Write-Host "Working directory: $scriptDir"

if (-not (Test-AdminRights)) {
  Write-Host "ERROR: Administrator rights required"
  Write-Host "Please run PowerShell as Administrator"
  Read-Host -Prompt "Press Enter to continue"
  exit 1
}

if (-not (Test-Path $CertPath)) {
  Write-Host "ERROR: Certificate not found at $CertPath"
  Read-Host -Prompt "Press Enter to continue"
  exit 1
}

if (-not (Test-Path $MsixPath)) {
  Write-Host "ERROR: MSIX not found at $MsixPath"
  Read-Host -Prompt "Press Enter to continue"
  exit 1
}

Write-Host "Installing Certificate to Trusted Root Store..."
try {
  $cert = Import-PfxCertificate -FilePath $CertPath -CertStoreLocation "Cert:\CurrentUser\My" -Password $CertPassword
  Write-Host "Certificate imported to Personal store"
  
  if (Test-Path $CerPath) {
    Import-Certificate -FilePath $CerPath -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null
    Write-Host "Certificate imported to Trusted Root store"
  } else {
    Write-Host "WARNING: Public certificate file not found, skipping trusted root installation"
  }
  
  Write-Host "Subject: $($cert.Subject)"
} catch {
  Write-Host "ERROR: Certificate installation failed"
  Write-Host "Details: $_"
  Read-Host -Prompt "Press Enter to continue"
  exit 1
}

Write-Host "Installing VardyParty..."
try {
  Add-AppxPackage -Path $MsixPath
  Write-Host "Application installed successfully"
} catch {
  Write-Host "ERROR: MSIX installation failed"
  Write-Host "Details: $_"
  Read-Host -Prompt "Press Enter to continue"
  exit 1
}

$installed = Get-AppxPackage -Name "*VardyParty*" -ErrorAction SilentlyContinue
if ($installed) {
  Write-Host "Success! VardyParty is installed"
  Write-Host "Version: $($installed.Version)"
}

Write-Host "Installation complete!"
Read-Host -Prompt "Press Enter to continue"
