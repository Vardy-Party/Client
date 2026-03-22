# VardyParty Windows Installation Script
# This script installs the VardyParty MSIX package with certificate trust setup

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = Get-Location }

$CerPath = Join-Path $scriptDir "vardyparty.cer"

# Find MSIX by pattern — filename includes version/build number
$MsixFile = Get-ChildItem -Path $scriptDir -Filter "VardyParty-windows*.msix" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$MsixPath = if ($MsixFile) { $MsixFile.FullName } else { $null }

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

if (-not (Test-Path $CerPath)) {
  Write-Host "ERROR: Public certificate file not found at $CerPath"
  Read-Host -Prompt "Press Enter to continue"
  exit 1
}

if (-not $MsixPath) {
  Write-Host "ERROR: No VardyParty-windows*.msix found in $scriptDir"
  Read-Host -Prompt "Press Enter to continue"
  exit 1
}
Write-Host "Found MSIX: $(Split-Path $MsixPath -Leaf)"

Write-Host "Installing Certificate to System Stores..."
try {
  # Import to LocalMachine Root for MSIX trust (requires admin)
  $cert = Import-Certificate -FilePath $CerPath -CertStoreLocation "Cert:\LocalMachine\Root"
  Write-Host "Certificate imported to Trusted Root Certification Authorities"
  
  # Import to TrustedPeople for MSIX installation (requires admin)
  Import-Certificate -FilePath $CerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
  Write-Host "Certificate imported to Trusted People"
  
  Write-Host "Subject: $($cert.Subject)"
  Write-Host "Thumbprint: $($cert.Thumbprint)"
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
