# Linux Support for VardyParty

## Current Status

As of .NET 10, .NET MAUI does not officially support Linux as a target platform. However, VardyParty has been structured to enable Linux support through the following approach:

### Architecture

- **VardyParty.Core**: Platform-agnostic .NET 10 library containing all business logic, services, and models
- **VardyParty**: MAUI application for Windows, macOS, iOS, and Android
- **Future: VardyParty.Linux**: Linux-specific UI application (to be implemented)

### What's Currently Available

The CI/CD pipeline now builds **VardyParty.Core** as self-contained binaries for:
- **linux-x64**: For x86_64 Linux systems
- **linux-arm64**: For ARM64 Linux systems (e.g., Raspberry Pi 4/5)

These binaries include:
- All business logic and services
- Configuration management (appsettings.json with Auth0 and API settings)
- Self-contained .NET runtime (no .NET installation required)

## Next Steps for Full Linux Application

To create a complete Linux application with UI, you have several options:

### Option 1: Avalonia UI (Recommended)

Create a new project `VardyParty.Linux` using [Avalonia UI](https://avaloniaui.net/), which provides:
- Cross-platform XAML-based UI (Windows, macOS, Linux)
- Modern, performant UI framework
- Good integration with .NET 10
- WebView support via `AvaloniaWebView`

**Implementation Steps:**
1. Create new Avalonia project: `VardyParty.Linux`
2. Reference `VardyParty.Core` for business logic
3. Implement Linux-specific video player using `libVLC` or `mpv`
4. Host Blazor components using AvaloniaWebView
5. Update CI/CD to build Avalonia app for Linux

### Option 2: GTK# / GtkSharp

Use [GtkSharp](https://github.com/GtkSharp/GtkSharp) for native Linux UI:
- Native Linux look and feel
- Mature and stable
- WebView support via `WebKitGtk`

**Implementation Steps:**
1. Create new GTK# project: `VardyParty.Linux`
2. Reference `VardyParty.Core` for business logic
3. Implement video player using GStreamer
4. Host Blazor components using WebKitGtk WebView
5. Update CI/CD to build GTK app for Linux

### Option 3: Electron.NET

Use [Electron.NET](https://github.com/ElectronNET/Electron.NET) for a web-based approach:
- Familiar web technologies
- Chromium-based WebView
- Cross-platform consistency

**Implementation Steps:**
1. Create ASP.NET Core Blazor Server project
2. Reference `VardyParty.Core` for business logic
3. Package with Electron.NET for Linux
4. Update CI/CD to build Electron app for Linux

## Video Playback on Linux

Since VardyParty streams HLS video, you'll need a video player that supports M3U8/HLS:

### Recommended: libVLC

```csharp
public class LinuxVideoPlayerService : INativeVideoPlayerService
{
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;

    public async Task<PlaybackResult> PlayVideoAsync(
        string m3u8Url, 
        string refererUrl, 
        string title,
        Func<Task>? onNextStreamRequested = null)
    {
        _libVLC = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVLC);
        
        var media = new Media(_libVLC, new Uri(m3u8Url));
        media.AddOption($":http-referrer={refererUrl}");
        
        _mediaPlayer.Play(media);
        
        return new PlaybackResult { Success = true };
    }

    public PlaybackMetrics? GetCurrentMetrics()
    {
        // Implement metrics retrieval from libVLC
        return null;
    }
}
```

### Alternative: MPV

MPV is another excellent option with HLS support:

```bash
# Install mpv
sudo apt install mpv

# Play HLS stream with referer
mpv --http-header-fields="Referer: https://example.com" "https://stream.m3u8"
```

You can integrate MPV via IPC or as a subprocess.

## Dependencies

Linux builds will require:

### Runtime Dependencies
- .NET 10 Runtime (included in self-contained builds)
- GTK3 or GTK4 (for UI frameworks)
- GStreamer or VLC (for video playback)
- WebKitGTK (for WebView support)

### Build Dependencies
```bash
# Ubuntu/Debian
sudo apt update
sudo apt install -y \
    dotnet-sdk-10.0 \
    libgtk-3-dev \
    libwebkit2gtk-4.0-dev \
    libvlc-dev \
    gstreamer1.0-plugins-base \
    gstreamer1.0-plugins-good \
    gstreamer1.0-plugins-bad \
    gstreamer1.0-libav

# Fedora/RHEL
sudo dnf install -y \
    dotnet-sdk-10.0 \
    gtk3-devel \
    webkit2gtk3-devel \
    vlc-devel \
    gstreamer1-plugins-base \
    gstreamer1-plugins-good \
    gstreamer1-plugins-bad-free \
    gstreamer1-libav
```

## Distribution Formats

Consider packaging for multiple Linux distribution formats:

### AppImage (Recommended)
- Single-file executable
- No installation required
- Works on most Linux distributions

### Flatpak
- Sandboxed application
- Available on Flathub
- Modern Linux packaging

### Snap
- Ubuntu's app store
- Cross-distribution support

### .deb (Debian/Ubuntu)
```bash
# Build .deb package
dpkg-deb --build vardyparty-linux-amd64
```

### .rpm (Fedora/RHEL/openSUSE)
```bash
# Build .rpm package
rpmbuild -ba vardyparty.spec
```

## CI/CD Updates

The workflows have been updated:

### CI Workflow (.github/workflows/ci.yml)
- ✅ Builds VardyParty.Core for linux-x64
- ✅ Builds VardyParty.Core for linux-arm64
- ⏳ TODO: Build Linux UI application when created

### CD Workflow (.github/workflows/cd.yml)
- ✅ Generates appsettings.json with secrets
- ✅ Creates self-contained linux-x64 package
- ✅ Creates self-contained linux-arm64 package
- ✅ Uploads tar.gz artifacts
- ⏳ TODO: Create AppImage/Flatpak/Snap packages

## Testing Linux Builds Locally

### Extract and run the Core library:
```bash
# Download the artifact
tar -xzf VardyParty-linux-x64.tar.gz -C vardyparty-linux

# The Core library is not directly executable yet
# It needs a UI host application
```

### When Linux UI is implemented:
```bash
# Extract
tar -xzf VardyParty-linux-x64.tar.gz -C vardyparty-linux

# Make executable
chmod +x vardyparty-linux/VardyParty.Linux

# Run
./vardyparty-linux/VardyParty.Linux
```

## Recommended Next Steps

1. **Choose UI Framework**: Decide between Avalonia, GTK#, or Electron.NET
2. **Create Linux Project**: Set up `VardyParty.Linux` project
3. **Implement Video Player**: Integrate libVLC or MPV for HLS playback
4. **Update CI/CD**: Add Linux UI build and packaging steps
5. **Test on Linux**: Verify on Ubuntu, Fedora, and Arch Linux
6. **Create Packages**: Generate AppImage, Flatpak, or Snap packages
7. **Document Installation**: Update README with Linux installation instructions

## References

- [.NET MAUI Linux Status](https://github.com/dotnet/maui/issues/2023)
- [Avalonia UI Documentation](https://docs.avaloniaui.net/)
- [GtkSharp Documentation](https://github.com/GtkSharp/GtkSharp)
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp)
- [AppImage Documentation](https://appimage.org/)

---

**Note**: This document will be updated as Linux support progresses. The current implementation provides the foundation (VardyParty.Core) for a Linux application, but a UI layer still needs to be implemented.
