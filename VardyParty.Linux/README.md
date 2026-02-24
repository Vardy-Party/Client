# VardyParty for Linux

VardyParty native Linux application built with Avalonia UI.

## Architecture

- **UI Framework**: Avalonia 11.2 (cross-platform XAML-based UI)
- **Video Player**: LibVLC (supports HLS/M3U8 streaming)
- **Shared Logic**: VardyParty.Core library
- **Target Platforms**: Linux x64, Linux ARM64

## Prerequisites

### Runtime Dependencies

```bash
# Ubuntu/Debian
sudo apt install libvlc5 vlc-plugin-base

# Fedora/RHEL
sudo dnf install vlc-core

# Arch Linux
sudo pacman -S vlc
```

### Development Dependencies

```bash
# Ubuntu/Debian
sudo apt install dotnet-sdk-10.0 libvlc-dev

# Fedora/RHEL
sudo dnf install dotnet-sdk-10.0 vlc-devel

# Arch Linux
sudo pacman -S dotnet-sdk vlc
```

## Building from Source

```bash
# Clone the repository
git clone https://github.com/Vardy-Party/Client.git
cd Client/VardyParty.Linux

# Restore dependencies
dotnet restore

# Build for your architecture
dotnet build -c Release

# Or publish self-contained
dotnet publish -c Release -r linux-x64 --self-contained
```

## Running

### From Build Output
```bash
cd bin/Release/net10.0/linux-x64
./VardyParty
```

### From Published Output
```bash
cd bin/Release/net10.0/linux-x64/publish
./VardyParty
```

## Configuration

Create or edit `appsettings.json` in the application directory:

```json
{
  "Auth0": {
    "Domain": "your-domain.auth0.com",
    "ClientId": "your-client-id",
    "Audience": "your-audience",
    "Scope": "openid profile email",
    "CallbackScheme": "vardyparty",
    "RedirectUri": "vardyparty://callback",
    "PostLogoutRedirectUri": "vardyparty://callback",
    "TokenLeewaySeconds": 60,
    "RequiredRoleClaimType": "https://vardyparty.com/roles",
    "RequiredRole": "user"
  },
  "Api": {
    "HeadlessBaseUrl": "https://api.vardyparty.com"
  }
}
```

## Distribution Packages

### AppImage (Recommended)
Self-contained single-file executable that runs on most Linux distributions.

```bash
# Download
wget https://github.com/Vardy-Party/Client/releases/latest/download/VardyParty-x86_64.AppImage

# Make executable
chmod +x VardyParty-x86_64.AppImage

# Run
./VardyParty-x86_64.AppImage
```

### Flatpak
Available on Flathub (coming soon):

```bash
flatpak install flathub com.vardyparty.VardyParty
flatpak run com.vardyparty.VardyParty
```

### Snap
Available on Snapcraft (coming soon):

```bash
sudo snap install vardyparty
vardyparty
```

## Features

- ✅ Browse live football matches
- ✅ Stream selection and health checking
- ✅ HLS/M3U8 video playback via LibVLC
- ✅ Auth0 authentication
- ✅ Dark theme UI
- ⏳ WebView integration for Blazor components (in progress)
- ⏳ TV/Remote control support

## Troubleshooting

### Video Playback Issues

1. **Ensure VLC is installed:**
   ```bash
   vlc --version
   ```

2. **Check VLC plugins:**
   ```bash
   # Ubuntu/Debian
   sudo apt install vlc-plugin-base vlc-plugin-video-output
   
   # Fedora
   sudo dnf install vlc-plugins-base vlc-plugins-video-output
   ```

3. **Test HLS playback directly:**
   ```bash
   vlc https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8
   ```

### Display Issues

If you encounter display issues:

```bash
# Try running with X11 backend
export GDK_BACKEND=x11
./VardyParty

# Or with Wayland
export GDK_BACKEND=wayland
./VardyParty
```

### Missing Dependencies

```bash
# Check for missing libraries
ldd ./VardyParty

# Install missing dependencies
# Ubuntu/Debian
sudo apt install libicu72 libssl3

# Fedora
sudo dnf install icu openssl-libs
```

## Development

### Project Structure

```
VardyParty.Linux/
├── Program.cs              # Entry point
├── App.axaml              # Application definition
├── App.axaml.cs           # DI and configuration setup
├── MainWindow.axaml       # Main window UI
├── MainWindow.axaml.cs    # Main window code-behind
├── Services/
│   └── LinuxVideoPlayerService.cs  # VLC-based video player
├── Assets/                # Images and icons
└── wwwroot/              # Shared with VardyParty (Blazor assets)
```

### Adding Features

1. Shared business logic goes in `VardyParty.Core`
2. Linux-specific UI goes in this project
3. Blazor components are shared from `VardyParty/Components`

## Contributing

See the main repository's [CONTRIBUTING.md](../CONTRIBUTING.md) for guidelines.

## License

See [LICENSE](../LICENSE) in the repository root.

## Links

- [Main Repository](https://github.com/Vardy-Party/Client)
- [Avalonia UI](https://avaloniaui.net/)
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp)
- [Issue Tracker](https://github.com/Vardy-Party/Client/issues)
