# Linux Support for VardyParty

## ✅ Current Status - IMPLEMENTED!

VardyParty now has **native Linux support** via the **VardyParty.Linux** project built with Avalonia UI!

### What's Available

✅ **Full Native Application** for Linux  
✅ **Avalonia UI 11.2** - Modern cross-platform XAML framework  
✅ **LibVLC Video Player** - Full HLS/M3U8 streaming support  
✅ **VardyParty.Core Integration** - Shares all business logic with other platforms  
✅ **Self-Contained Builds** - No .NET installation required  
✅ **x64 and ARM64 Support** - Works on PCs and Raspberry Pi

## Architecture

```
VardyParty.Linux (NEW!)
├── Avalonia UI Frontend
├── LibVLC Video Player
└── References VardyParty.Core

VardyParty.Core
├── Business Logic
├── Auth0 Integration
├── Stream Health Checking
└── API Services

VardyParty (MAUI)
└── Windows, macOS, iOS, Android
```

## Installation

### Option 1: Download Pre-built Package

```bash
# Download from releases
wget https://github.com/Vardy-Party/Client/releases/latest/download/VardyParty-linux-x64.tar.gz

# Extract
tar -xzf VardyParty-linux-x64.tar.gz -C vardyparty-linux

# Install VLC (required for video playback)
# Ubuntu/Debian
sudo apt install vlc libvlc5

# Fedora/RHEL
sudo dnf install vlc

# Arch Linux
sudo pacman -S vlc

# Run the application
cd vardyparty-linux
chmod +x VardyParty
./VardyParty
```

### Option 2: Build from Source

See [VardyParty.Linux/README.md](../VardyParty.Linux/README.md) for detailed build instructions.

## Features

✅ **Browse Live Matches** - See all available football matches  
✅ **Stream Health Checking** - Automatic quality verification  
✅ **HLS Video Playback** - LibVLC handles M3U8 streams perfectly  
✅ **Auth0 Authentication** - Secure login integration  
✅ **Dark Theme UI** - Modern Avalonia Fluent theme  
✅ **Multi-Architecture** - x64 and ARM64 builds  

🚧 **In Progress:**
- WebView integration for Blazor components
- TV remote control support
- AppImage/Flatpak/Snap packages

## Dependencies

### Runtime Requirements

```bash
# Ubuntu 22.04 / Debian 12
sudo apt install vlc libvlc5

# Ubuntu 20.04 / Debian 11
sudo apt install vlc libvlc-dev

# Fedora 38+
sudo dnf install vlc

# Arch Linux
sudo pacman -S vlc

# openSUSE
sudo zypper install vlc
```

### Development Requirements

```bash
# Ubuntu/Debian
sudo apt install dotnet-sdk-10.0 libvlc-dev

# Fedora
sudo dnf install dotnet-sdk-10.0 vlc-devel

# Arch Linux
sudo pacman -S dotnet-sdk vlc
```

## CI/CD Pipeline

### ✅ CI Workflow (ci.yml)
- Builds VardyParty.Linux for linux-x64
- Builds VardyParty.Linux for linux-arm64
- Installs libVLC dependencies
- Runs automated tests

### ✅ CD Workflow (cd.yml)
- Generates appsettings.json with secrets
- Creates self-contained linux-x64 package
- Creates self-contained linux-arm64 package
- Uploads tar.gz artifacts to releases

## Project Structure

```
VardyParty.Linux/
├── Program.cs                      # Entry point
├── App.axaml / App.axaml.cs       # Avalonia app + DI setup
├── MainWindow.axaml / .cs         # Main window UI
├── Services/
│   └── LinuxVideoPlayerService.cs # LibVLC video player
├── Assets/                         # Icons and images
├── README.md                       # Linux-specific documentation
└── VardyParty.Linux.csproj        # Project file

Dependencies:
├── Avalonia 11.2                   # UI Framework
├── LibVLCSharp 3.9                 # Video playback
├── VardyParty.Core                 # Shared business logic
└── Microsoft.Extensions.*          # Configuration & DI
```

## Supported Distributions

Tested and working on:
- ✅ Ubuntu 22.04 LTS (Jammy)
- ✅ Ubuntu 24.04 LTS (Noble)
- ✅ Debian 12 (Bookworm)
- ✅ Fedora 39+
- ✅ Arch Linux
- ✅ Raspberry Pi OS (ARM64)

Should work on:
- Pop!_OS 22.04+
- Linux Mint 21+
- openSUSE Tumbleweed
- Manjaro Linux
- EndeavourOS

## Troubleshooting

### Video Playback Issues

**Problem**: "VLC plugins not found" error

```bash
# Solution: Install VLC plugins
sudo apt install vlc-plugin-base vlc-plugin-video-output

# Verify VLC works
vlc --version
```

**Problem**: Stream won't play

```bash
# Test VLC directly with a stream
vlc https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8

# Check libVLC libraries
ldconfig -p | grep libvlc
```

### Display Issues

**Problem**: Blank window or rendering issues

```bash
# Try X11 backend
export GDK_BACKEND=x11
./VardyParty

# Or force Wayland
export GDK_BACKEND=wayland
./VardyParty
```

### Missing Dependencies

```bash
# Check for missing libraries
ldd ./VardyParty

# Install common missing deps (Ubuntu/Debian)
sudo apt install libicu72 libssl3 zlib1g

# Fedora
sudo dnf install icu openssl-libs zlib
```

## Future Enhancements

### Short Term
- [ ] Integrate AvaloniaWebView for Blazor components
- [ ] Add AppImage packaging
- [ ] Desktop file and icon installation
- [ ] System tray integration

### Medium Term
- [ ] Flatpak package on Flathub
- [ ] Snap package on Snapcraft
- [ ] .deb package for Ubuntu/Debian
- [ ] .rpm package for Fedora/RHEL

### Long Term
- [ ] TV remote control support (for Linux TV boxes)
- [ ] Hardware acceleration for video
- [ ] Wayland native support improvements

## Why Avalonia?

From the [Avalonia blog](https://avaloniaui.net/blog/net-maui-is-coming-to-linux-and-the-browser-powered-by-avalonia):

> ".NET MAUI is coming to Linux and the Browser powered by Avalonia"

Avalonia is:
- ✅ The future backend for MAUI on Linux
- ✅ Production-ready and mature
- ✅ True cross-platform (Windows, macOS, Linux, iOS, Android, WASM)
- ✅ XAML-based like MAUI (easy to learn)
- ✅ High performance with Skia rendering
- ✅ Active community and regular updates

By building VardyParty.Linux with Avalonia today, we're aligned with where MAUI is heading!

## Development

Want to contribute? See:
- [VardyParty.Linux/README.md](../VardyParty.Linux/README.md) - Detailed dev guide
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Contribution guidelines

## References

- [Avalonia UI Documentation](https://docs.avaloniaui.net/)
- [LibVLCSharp Documentation](https://code.videolan.org/videolan/LibVLCSharp)
- [.NET MAUI + Avalonia Announcement](https://avaloniaui.net/blog/net-maui-is-coming-to-linux-and-the-browser-powered-by-avalonia)
- [VardyParty Repository](https://github.com/Vardy-Party/Client)

---

**🎉 Linux support is now live!** Download the latest release and join the party on Linux!
