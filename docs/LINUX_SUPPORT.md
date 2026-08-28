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
**not** need the `android` workload to run the Desktop head — but you must
pin HomeUi to `net11.0` in the **environment**, not rely on the Desktop
head's `ProjectReference` pin alone. HomeUi on Linux defaults to
`net11.0;net11.0-android` (so APK-from-Linux restore has the android TFM),
and restore evaluates HomeUi with those defaults regardless of the
`ProjectReference` `AdditionalProperties` — a plain
`dotnet run --project VardyParty.Desktop/...` therefore fails with
NETSDK1147 ("workloads must be installed: android") when the android
workload is absent. CI's build-desktop job, the unit-test job and
`scripts/launch-linux-app.cmd` all use the same fix: set
`HomeUiTargetFrameworks=net11.0` for restore AND build so HomeUi's android
target never enters the desktop build graph.

```bash
dotnet workload install maui-tizen
dotnet restore VardyParty.Desktop/VardyParty.Desktop.csproj -p:HomeUiTargetFrameworks=net11.0
dotnet run --project VardyParty.Desktop/VardyParty.Desktop.csproj -c Release --no-restore -p:HomeUiTargetFrameworks=net11.0
```

> Pass the pin as `-p:` per command — do NOT `export HomeUiTargetFrameworks`
> into your shell. A lingering export silently drops HomeUi's android target
> from every later Android build in that shell (Linux/WSL evaluation only)
> and fails restore with cryptic NETSDK1005/1147 errors. `package-android.ps1`
> neutralises the variable defensively, but plain `dotnet build` commands
> won't.

For video playback:

```bash
sudo apt install vlc libvlc-dev
```

From Windows/WSL, `scripts/launch-linux-app.cmd` builds and launches the
Desktop head inside WSLg. That script calls `$HOME/.dotnet/dotnet` directly,
so step 1 is enough even if `which dotnet` is still 10. It pins
`HomeUiTargetFrameworks=net11.0` itself (no android workload needed) and, if
NETSDK1147 still surfaces, prints the fallback remedy
(`dotnet workload install android`).
