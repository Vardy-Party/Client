# Linux Support

> **Superseded.** The Avalonia 11 `VardyParty.Linux` app this document
> described has been deleted. Linux is now served by **`VardyParty.Desktop`**:
> the shared .NET MAUI XAML homepage (`VardyParty.HomeUi`) drawn by the
> Avalonia 12 preview backend, with the Auth0 device-code/QR sign-in and
> LibVLC playback ported over from the old head.
>
> See [architecture/homepage-maui-avalonia.md](architecture/homepage-maui-avalonia.md)
> for the full stack description, build instructions and CI shape.

The Desktop head targets **net11.0**. A .NET 10 SDK (Ubuntu apt or an old
`~/.dotnet`) fails with `NETSDK1045`. Use the **.NET 11 preview SDK**, same
band as the Windows MAUI head: `11.0.100-preview.7` or later
(`11.0.100-preview.7.26381.103` is the known-good pin).

## Install the .NET 11 preview SDK (Ubuntu)

Do **not** retarget the repo to net10. Install the preview SDK locally so it
does not fight distro packages.

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel 11.0 --quality preview --install-dir "$HOME/.dotnet"
```

To pin the same SDK as Windows/CI:

```bash
/tmp/dotnet-install.sh --version 11.0.100-preview.7.26381.103 --install-dir "$HOME/.dotnet"
```

Put that host **first** on `PATH` (otherwise `/usr/lib/dotnet` 10.x wins):

```bash
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
source ~/.bashrc
```

Check:

```bash
which dotnet
dotnet --version    # 11.0.100-preview.7… not 10.0.x
dotnet --list-sdks
```

Official downloads: <https://dotnet.microsoft.com/download/dotnet/11.0>.
Scripted install: <https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual>.

## Run the Desktop head

The `maui-tizen` workload carries the plain-TFM MAUI SDK on Linux. You do
**not** need the `android` workload to run the Desktop head: that project
pins HomeUi to `net11.0` on its `ProjectReference`. Restoring
`VardyParty.HomeUi.csproj` by itself on Linux still lists `net11.0-android`
(for APK-from-Linux); use the Desktop project or `-f net11.0`.

```bash
dotnet workload install maui-tizen
dotnet run --project VardyParty.Desktop/VardyParty.Desktop.csproj -c Release
```

For video playback:

```bash
sudo apt install vlc libvlc-dev
```

From Windows/WSL, `scripts/launch-linux-app.cmd` builds and launches the
Desktop head inside WSLg. That script calls `$HOME/.dotnet/dotnet` directly,
so step 1 is enough even if `which dotnet` is still 10.
