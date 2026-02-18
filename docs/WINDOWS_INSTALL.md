# VardyParty Windows Installation Guide

This package contains everything needed to install VardyParty on Windows 10+.

## Package Contents

- **VardyParty-windows.msix** - The application installer
- **vardyparty.pfx** - Self-signed certificate (password protected)
- **vardyparty.cer** - Public certificate (for viewing/verification)
- **AppxManifest.xml** - Package metadata and configuration
- **install.ps1** - Automated installation script (recommended)
- **INTEGRITY.txt** - SHA256 hashes for file verification

## Installation Method


1. **Right-click Terminal** and select **"Run as Administrator"**
2. Navigate to the extracted package folder
3. Run: `pwsh.exe -ExecutionPolicy Unrestricted`
4. Run: `.\install.ps1`
5. Answer `R` for `Run Once` (if asked)
6. The script will automatically:
   - Install the certificate to system stores (LocalMachine\Root and LocalMachine\TrustedPeople)
   - Install the MSIX package

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
