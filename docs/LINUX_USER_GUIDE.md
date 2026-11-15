# Linux user guide: install and run Vardy Party

This guide is for people who want to **run** the Linux client. To build it
from source, see [LINUX_DEVELOPER_GUIDE.md](LINUX_DEVELOPER_GUIDE.md). Design
notes and troubleshooting live in [LINUX_SUPPORT.md](LINUX_SUPPORT.md).

Everything here matches what `cd.yml` ships on the GitHub **Releases** page
(for example `3.5.1-b249`): for Linux that is **only** the two snaps (x64 and
ARM64), each with its `.minisig`, plus `minisign.pub`. The x64 snap is built on
`ubuntu-24.04` and the ARM64 snap natively on `ubuntu-24.04-arm`; CI's runtime
smoke test runs on Ubuntu 24.04 x64. Other distros are listed as examples and
are **not tested in CI**.

## What you need

| Need | Why |
|------|-----|
| A 64-bit Linux desktop (X11 or Wayland), Ubuntu 24.04 or newer recommended | The app is built on Ubuntu 24.04 (glibc of 24.04). Older distros may fail to start the binary. |
| LibVLC (VLC's library and plugins) | All video playback goes through LibVLC. The snap bundles it; a self-built publish needs it from your distro. |
| PulseAudio, or PipeWire with its Pulse layer | Stream audio uses `--aout=pulse` by default (see [audio](#audio)). |
| ICU and fonts (`libicu`, `fontconfig`, a font such as DejaVu) | .NET needs ICU; the UI needs fonts. Most desktop installs already have them. |
| Network access | Streams, sign-in and the Vardy Party API. |

You do **not** need the .NET SDK or runtime to run a release build: both
packages are self-contained.

## Option 1: snap (x64 or ARM64)

Each release on the GitHub **Releases** page carries
`VardyParty-linux-x64-v<version>+<build>.snap` (and
`VardyParty-linux-arm64-v<version>+<build>.snap` for ARM64), each with its
signature `…snap.minisig`, plus `minisign.pub`. The examples below use x64;
swap in `arm64` on ARM machines.

The snap is `base: core24`, `confinement: classic` and `grade: devel`, and
it bundles LibVLC and its codecs (`libvlc5`, `vlc-plugin-base`,
`libavcodec60`, `libpulse0`, X11 libraries, `libicu74`, DejaVu fonts…), so
you do not need VLC installed.

1. Install snapd if your distro does not ship it (Ubuntu does).
2. Optional but recommended: check the signature with
   [minisign](https://jedisct1.github.io/minisign/):

   ```bash
   minisign -Vm VardyParty-linux-x64-v<version>+<build>.snap -p minisign.pub
   ```

3. Install it. `--dangerous` is needed because the snap is not from the Snap
   Store; `--classic` because it uses classic confinement:

   ```bash
   sudo snap install --classic --dangerous ./VardyParty-linux-x64-v<version>+<build>.snap
   ```

4. Start **Vardy Party** from your app menu, or run `vardyparty`.

To upgrade, install the newer `.snap` the same way. To remove it:
`sudo snap remove vardyparty`.

## Option 2: run a self-built publish (no snap)

The snap is the supported way to install. Normal releases do **not** include a
Linux tarball. (`release.yml` can pack `VardyParty-linux-x64-<tag>.tar.gz` and
`VardyParty-linux-arm64-<tag>.tar.gz`, cross-published on x64 `ubuntu-24.04`,
but it only runs for a rare `v*` tag push or a manual run.) If you want to run
without snapd, build a self-contained publish from source as described in
[LINUX_DEVELOPER_GUIDE.md §9](LINUX_DEVELOPER_GUIDE.md#9-publish-a-self-contained-build),
then:

1. Install LibVLC. A self-built publish (or a `v*` tarball) uses **your
   distro's LibVLC**. These are the packages CI installs for its runtime test:

   ```bash
   # Ubuntu / Debian (tested in CI on Ubuntu 24.04)
   sudo apt update
   sudo apt install vlc libvlc-dev
   ```

   Other distros (not tested in CI; install VLC from your normal repositories):

   ```bash
   # Fedora (VLC comes from RPM Fusion)
   sudo dnf install vlc
   # Arch Linux
   sudo pacman -S vlc
   ```

2. Run the published binary:

   ```bash
   VardyParty.Linux/bin/Release/net11.0/linux-x64/publish/VardyPartyLinux
   ```

   Use the `linux-arm64` folder if you published with `-r linux-arm64`.

## First run

- Sign in with the QR code / device code shown on the home page.
- Playback opens inside the app window; the menu has **Fullscreen** (also
  **F11**). **Escape** closes menus, then leaves fullscreen, then closes
  playback.

## Audio

The client asks LibVLC for PulseAudio output (PipeWire's Pulse layer works).
If audio is silent or crackles, try another output before starting the app:

```bash
VARDYPARTY_LINUX_VLC_AOUT=alsa vardyparty      # or: pulse (default), any
```

Check that a default sink exists with `pactl info`.

## WSL (Windows Subsystem for Linux)

The client runs under WSLg. If fullscreen misbehaves there, start it with
`VARDYPARTY_LINUX_FULLSCREEN_AS_MAXIMIZED=1` to maximise instead.

## Troubleshooting

| Symptom | Check |
|---------|-------|
| Self-built publish starts but no video | Is LibVLC installed (`vlc --version`)? Install `vlc libvlc-dev` as above. |
| `GLIBC_… not found` when starting | Your distro is older than Ubuntu 24.04's glibc. Use a newer distro. |
| App starts but text is missing or boxes | Install fonts (`fonts-dejavu-core` on Ubuntu/Debian) and `fontconfig`. |
| No sound | See [Audio](#audio). |

More detail: [LINUX_SUPPORT.md](LINUX_SUPPORT.md).
