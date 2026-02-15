param(
    [string]$OutPath = "certs/vardyparty.pfx",
    [string]$Password = "P@ssw0rd!"
)

# Create certs directory
$dir = Split-Path $OutPath
if (-not (Test-Path -Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

Write-Host "Generating self-signed certificate at $OutPath"

# Use powershell New-SelfSignedCertificate and export
$cert = New-SelfSignedCertificate -Subject "CN=VardyParty" -Type CodeSigningCert -KeyExportPolicy Exportable -KeySpec Signature -NotAfter (Get-Date).AddYears(10) -CertStoreLocation "Cert:\CurrentUser\My"

# Convert provided password to SecureString for export
$securePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force

Export-PfxCertificate -Cert $cert -FilePath $OutPath -Password $securePassword

Write-Host "Generated cert. Please set environment variable MSIX_CERT_PASSWORD to the password used (not recommended in plaintext)."