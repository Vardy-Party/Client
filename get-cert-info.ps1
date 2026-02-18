# Get certificate Base64 and thumbprint

$certPath = "certs/vardyparty.pfx"
$certPassword = "8Hjipohd0G)*G9fh"

# Convert to Base64
$bytes = [System.IO.File]::ReadAllBytes($certPath)
$base64 = [System.Convert]::ToBase64String($bytes)

Write-Host "=== BASE64 CERTIFICATE (for WINDOWS_CERT_BASE64 secret) ===" -ForegroundColor Green
Write-Host $base64
Write-Host ""

# Get thumbprint
$securePassword = ConvertTo-SecureString -String $certPassword -AsPlainText -Force
$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2
$cert.Import($certPath, $securePassword, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet)

Write-Host "=== THUMBPRINT (for PackageCertificateThumbprint) ===" -ForegroundColor Green
Write-Host $cert.Thumbprint
Write-Host ""

Write-Host "=== CERTIFICATE INFO ===" -ForegroundColor Green
Write-Host "Subject: $($cert.Subject)"
Write-Host "Issuer: $($cert.Issuer)"
Write-Host "Valid from: $($cert.NotBefore)"
Write-Host "Valid to: $($cert.NotAfter)"
