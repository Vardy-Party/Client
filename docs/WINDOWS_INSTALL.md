# VardyParty Windows Installation Guide

This package contains everything needed to install VardyParty on Windows 10+.

## Package Contents

- **VardyParty-windows.msix** - The application installer
- **vardyparty.pfx** - Self-signed certificate (password protected)
- **vardyparty.cer** - Public certificate (for viewing/verification)
- **AppxManifest.xml** - Package metadata and configuration
- **install.ps1** - Automated installation script (recommended)
- **INTEGRITY.txt** - SHA256 hashes for file verification

## Installation Methods

### Method 1: Automated Installation (Recommended)

1. Right-click `install.ps1` and select **"Run with PowerShell"**
   - Alternatively, open PowerShell as Administrator and run: `.\install.ps1`
2. When prompted, enter the certificate password
3. Follow the script prompts
4. The script will automatically install the certificate and MSIX package

### Method 2: Manual Installation

#### Step 1: Install the Certificate
1. Open PowerShell as Administrator
2. Run:
   ```powershell
   $password = ConvertTo-SecureString -String "YOUR_PASSWORD_HERE" -Force -AsPlainText
   Import-PfxCertificate -FilePath "vardyparty.pfx" -CertStoreLocation "Cert:\CurrentUser\My" -Password $password
   Import-Certificate -FilePath "vardyparty.cer" -CertStoreLocation "Cert:\CurrentUser\Root"
   ```

#### Step 2: Install the MSIX
1. Double-click `VardyParty-windows.msix`, or
2. Use PowerShell as Administrator:
   ```powershell
   Add-AppxPackage -Path "VardyParty-windows.msix"
   ```

## Verification

To verify file integrity before installation:

```powershell
Get-FileHash -Path "VardyParty-windows.msix" -Algorithm SHA256
Get-FileHash -Path "vardyparty.pfx" -Algorithm SHA256
```

Compare the output with the hashes in `INTEGRITY.txt`.

## Certificate Information

This package uses a **self-signed certificate** for testing purposes only.

To view certificate details:
- Double-click `vardyparty.cer` and select "View Certificate", or
- Right-click `vardyparty.pfx` and select "Properties"

**Security Note**: Self-signed certificates should only be used for testing/development. For production distribution, obtain a code-signing certificate from a trusted Certificate Authority.

## Troubleshooting

### "Administrator rights required" error
- Ensure you're running PowerShell as Administrator
- Right-click PowerShell → "Run as administrator"

### Certificate installation fails
- Verify the password is correct
- Ensure you have administrator privileges
- Try the manual method above

### MSIX installation fails (trust error)
- Ensure the certificate is installed to both:
  - Personal store (Cert:\CurrentUser\My)
  - Trusted Root store (Cert:\CurrentUser\Root)
- Run `Get-AppxPackageLog -ActivityID <ID>` for detailed error information
- Try uninstalling any previous version first

### "Windows Protected Your PC" warning
- This is normal for self-signed certificates
- Click "More info" → "Run anyway"

## Uninstalling

To uninstall VardyParty:

```powershell
Get-AppxPackage -Name "*VardyParty*" | Remove-AppxPackage
```

Or using the Settings app:
1. Settings → Apps → Installed apps
2. Search for "VardyParty"
3. Click the three dots → Uninstall

## Support

For issues or questions, please refer to the main repository documentation or open an issue on GitHub.
