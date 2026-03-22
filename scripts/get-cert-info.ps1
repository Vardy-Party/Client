# Get certificate Base64 and thumbprint

param(
    [string]$CertPath = (Join-Path $PSScriptRoot "..\certs\vardyparty.pfx"),
    [string]$Password = "P@ssw0rd!"
)

if (-not (Test-Path $CertPath)) {
    Write-Error "Certificate not found: $CertPath"
    exit 1
}

# Convert to Base64
$bytes = [System.IO.File]::ReadAllBytes($CertPath)
$base64 = [System.Convert]::ToBase64String($bytes)

Write-Host "=== BASE64 CERTIFICATE (for WINDOWS_CERT_BASE64 secret) ===" -ForegroundColor Green
Write-Host $base64
Write-Host ""

# Get thumbprint
$securePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
try {
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
        (Resolve-Path $CertPath).Path,
        $securePassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet
    )
} catch {
    Write-Error "Failed to load certificate: $_"
    exit 1
}

Write-Host "=== THUMBPRINT (for PackageCertificateThumbprint) ===" -ForegroundColor Green
Write-Host $cert.Thumbprint
Write-Host ""

Write-Host "=== CERTIFICATE INFO ===" -ForegroundColor Green
Write-Host "Subject: $($cert.Subject)"
Write-Host "Issuer: $($cert.Issuer)"
Write-Host "Valid from: $($cert.NotBefore)"
Write-Host "Valid to: $($cert.NotAfter)"
