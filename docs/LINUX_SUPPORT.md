# Linux Support

> **Superseded.** The Avalonia 11 `VardyParty.Linux` app this document
> described has been deleted. Linux is now served by **`VardyParty.Desktop`**:
> the shared .NET MAUI XAML homepage (`VardyParty.HomeUi`) drawn by the
> Avalonia 12 preview backend, with the Auth0 device-code/QR sign-in and
> LibVLC playback ported over from the old head.
>
> See [architecture/homepage-maui-avalonia.md](architecture/homepage-maui-avalonia.md)
> for the full stack description, build instructions and CI shape.

Quick start (requires the .NET 11 preview SDK and the `maui-tizen` workload,
which carries the plain-TFM MAUI SDK on Linux):

```bash
dotnet workload install maui-tizen
dotnet run --project VardyParty.Desktop/VardyParty.Desktop.csproj -c Release
```

For video playback install VLC's runtime libraries:

```bash
sudo apt install vlc libvlc-dev
```

From Windows/WSL, `scripts/launch-linux-app.cmd` builds and launches the
Desktop head inside WSLg.
