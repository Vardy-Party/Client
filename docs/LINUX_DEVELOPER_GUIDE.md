# Linux developer guide: build and run from source

This guide covers building `VardyParty.Linux` (the shared MAUI XAML home page
drawn by the Avalonia MAUI backend, with LibVLC playback) from source on
Linux. To install a release build instead, see
[LINUX_USER_GUIDE.md](LINUX_USER_GUIDE.md). Design notes, audio and
fullscreen behaviour, and secrets live in [LINUX_SUPPORT.md](LINUX_SUPPORT.md).

Every command below mirrors `.github/workflows/ci.yml`. `build-linux` and
`linux-runtime-smoke` are pinned to **Ubuntu 24.04**; the unit-test (`test`) and
`code-quality` jobs run on `ubuntu-latest`.

## 1. Remove preview and RC .NET bits first

If this machine ever had a .NET 11 preview or release candidate, remove it
before installing GA. Mixed preview SDKs, workload manifests and packs cause
`NETSDK1045`, workload-resolution errors and restores against preview
packages. See [Removing preview / RC .NET](#removing-preview--rc-net) below.

## 2. Install the .NET 11 GA SDK

The repo pins the SDK in `global.json`:

```json
{ "sdk": { "version": "11.0.100", "rollForward": "latestPatch" } }
```

so any `11.0.1xx` patch works (CI uses `11.0.1xx`). Install it per user with
Microsoft's script so it does not fight distro packages:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
# Exactly the pinned SDK:
/tmp/dotnet-install.sh --version 11.0.100 --install-dir "$HOME/.dotnet"
# Or the latest 11.0 GA patch:
/tmp/dotnet-install.sh --channel 11.0 --quality ga --install-dir "$HOME/.dotnet"
```

Put it first on `PATH` (otherwise a distro `/usr/lib/dotnet` may win):

```bash
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
source ~/.bashrc
which dotnet
dotnet --version      # 11.0.1xx, no "-preview" / "-rc"
```

## 3. Install the MAUI workload

On Linux the plain-TFM MAUI SDK comes from the `maui-tizen` workload. Pin the
same workload set as CI:

```bash
dotnet workload install maui-tizen --version 11.0.100.1
dotnet workload list
```

You do **not** need the `android` workload to build or run the Linux head. The
full format / analyzer check in [step 8](#8-run-the-tests-and-the-format-check)
does need it, because Code Quality also builds the Android head.

## 4. Get access to the private NuGet feed

`VardyParty.Streaming` uses the LocalService packages from GitHub Packages
(`https://nuget.pkg.github.com/Vardy-Party/index.json`). CI restores with a
`read:packages` token. Locally, add the source once with your GitHub login
and a personal access token that has `read:packages`:

```bash
dotnet nuget add source https://nuget.pkg.github.com/Vardy-Party/index.json \
  --name VardyParty \
  --username <github-login> \
  --password <token-with-read:packages> \
  --store-password-in-clear-text
```

(`--store-password-in-clear-text` is required on Linux, where NuGet cannot
encrypt credentials. It writes to `~/.nuget/NuGet/NuGet.Config`; keep that
file private.) Without this, restore fails on the LocalService packages.

## 5. Install the runtime libraries

The CI smoke test installs exactly these on Ubuntu 24.04:

```bash
sudo apt update
sudo apt install vlc libvlc-dev xvfb
```

`xvfb` is only needed for headless runs (step 7).

## 6. Restore, build and run

Always pass `-p:HomeUiTargetFrameworks=net11.0` on **each** command so the
HomeUi library is not restored for Android. Do **not** `export` it into your
shell: a lingering export silently breaks later Android builds in that shell.

```bash
dotnet restore VardyParty.Linux/VardyParty.Linux.csproj -p:HomeUiTargetFrameworks=net11.0
dotnet build   VardyParty.Linux/VardyParty.Linux.csproj -c Release --no-restore -p:HomeUiTargetFrameworks=net11.0
dotnet run --project VardyParty.Linux/VardyParty.Linux.csproj -c Release --no-restore -p:HomeUiTargetFrameworks=net11.0
```

The build output is `VardyParty.Linux/bin/Release/net11.0/VardyPartyLinux`.

Sign-in needs Auth0 settings. The committed `VardyParty.Linux/appsettings.json`
is a template; set your values in .NET user-secrets and patch them in at
build time (`-p:PatchAppSettings=true`), then `git restore` the template. See
[LINUX_SUPPORT.md → Secrets](LINUX_SUPPORT.md#secrets-auth0--api). Never commit
`appsettings.json` with secrets.

To try the UI without an account or API, start it with sample data:

```bash
VARDYPARTY_LINUX_SAMPLE_DATA=1 VardyParty.Linux/bin/Release/net11.0/VardyPartyLinux
```

From Windows, `scripts/launch-linux-app.ps1` builds and launches the Linux
head inside WSLg.

## 7. Reproduce the CI smoke test

CI starts the app under a virtual display and expects it to stay up for 20s
(exit code 124 from `timeout` is success):

```bash
VARDYPARTY_LINUX_SAMPLE_DATA=1 xvfb-run -a timeout 20s VardyParty.Linux/bin/Release/net11.0/VardyPartyLinux
echo $?   # 124 = still running after 20s = pass
```

## 8. Run the tests and the format check

Same as the CI unit-test job (it sets `HomeUiTargetFrameworks=net11.0` for the
whole job):

```bash
export HomeUiTargetFrameworks=net11.0   # only in a throwaway shell for tests
find tests -name '*Tests.csproj' -print0 | xargs -0 -n1 -I{} dotnet test "{}" -c Release
```

Code Quality (the same sequence as the `code-quality` job). It needs the
Android workload too, builds every non-test project with warnings as errors
(this also restores them), restores the test projects, and only then runs the
format check with `--no-restore`:

```bash
dotnet workload install maui-android maui-tizen --version 11.0.100.1

# 1. Analyzers: warnings as errors (excludes tests, HomeUi and Linux, like CI)
find . -name '*.csproj' -not -name '*Tests.csproj' \
  -not -name 'VardyParty.HomeUi.csproj' -not -name 'VardyParty.Linux.csproj' \
  -type f -print0 | while IFS= read -r -d '' proj; do
    dotnet build "$proj" -c Release \
      /p:TreatWarningsAsErrors=true /p:EnforceCodeStyleInBuild=true /p:AndroidKeyStore=false
  done

# 2. Restore the test projects so format can check them
find tests -name '*Tests.csproj' -type f -print0 | while IFS= read -r -d '' proj; do
  HomeUiTargetFrameworks=net11.0 dotnet restore "$proj"
done

# 3. Format check
dotnet format --verify-no-changes --no-restore --exclude VardyParty.HomeUi/ VardyParty.Linux/
```

Run as a single `dotnet format` on a fresh checkout, `--no-restore` fails on
unrestored projects, and the analyzer build (the part that most often fails
CI) is skipped.

## 9. Publish a self-contained build

This is the command the x64 snap job in `cd.yml` uses (the ARM64 snap job
uses `-r linux-arm64`):

```bash
dotnet publish VardyParty.Linux/VardyParty.Linux.csproj -f net11.0 -r linux-x64 -c Release \
  --self-contained true -p:PublishSingleFile=false -p:HomeUiTargetFrameworks=net11.0
```

Output: `VardyParty.Linux/bin/Release/net11.0/linux-x64/publish/`. Packaging
the snap itself (the generated `snapcraft.yaml`, wrapper and signing) is done
only by `cd.yml`; see the `package-linux-x64` job. Build on
Ubuntu 24.04 so the binary's glibc matches the snap base (`core24`).

## Removing preview / RC .NET

Do this before step 2 if any `11.0.100-preview.*` / `-rc.*` SDK, an
`11.0.0-preview.*` / `-rc.*` runtime, or a preview workload set is present.
Microsoft's `dotnet-core-uninstall` tool does not support Linux, so this is
manual.

### Find what is installed

```bash
which -a dotnet
dotnet --list-sdks
dotnet --list-runtimes
dotnet workload list
dotnet workload --info
echo "DOTNET_ROOT=$DOTNET_ROOT"
```

Also check for other installs:

```bash
apt list --installed 'dotnet*' 'aspnetcore*' 'netstandard*' 2>/dev/null
snap list 2>/dev/null | grep -i dotnet
ls -d /usr/share/dotnet /usr/lib/dotnet "$HOME/.dotnet" 2>/dev/null
```

### Remove preview workloads

With the preview SDK still active (run from a folder without the repo's
`global.json`, which would otherwise select the GA SDK):

```bash
cd ~
dotnet workload uninstall maui-tizen      # repeat for any other listed workload
dotnet workload clean --all               # remove workload packs left behind by any SDK
```

### Remove preview SDKs and runtimes

**Per-user install (`~/.dotnet`).** The simplest clean slate is to delete the
whole folder and reinstall GA (step 2):

```bash
rm -rf "$HOME/.dotnet"
```

Or remove only the preview pieces:

```bash
rm -rf "$HOME"/.dotnet/sdk/11.0.100-preview.* "$HOME"/.dotnet/sdk/11.0.100-rc.*
rm -rf "$HOME"/.dotnet/shared/Microsoft.NETCore.App/11.0.0-preview.* "$HOME"/.dotnet/shared/Microsoft.NETCore.App/11.0.0-rc.*
rm -rf "$HOME"/.dotnet/shared/Microsoft.AspNetCore.App/11.0.0-preview.* "$HOME"/.dotnet/shared/Microsoft.AspNetCore.App/11.0.0-rc.*
rm -rf "$HOME"/.dotnet/host/fxr/11.0.0-preview.* "$HOME"/.dotnet/host/fxr/11.0.0-rc.*
rm -rf "$HOME"/.dotnet/sdk-manifests/11.0.100-preview.* "$HOME"/.dotnet/sdk-manifests/11.0.100-rc.*
```

**Distro or Microsoft packages.** Remove any preview/RC package that
`apt list --installed` showed, for example:

```bash
sudo apt remove 'dotnet-sdk-11.0*' 'dotnet-runtime-11.0*' 'aspnetcore-runtime-11.0*'
sudo apt autoremove
```

**Snap.** If `snap list` showed a .NET snap: `sudo snap remove dotnet-sdk`.

### Clear cached preview packages (optional)

Restores may keep resolving preview NuGet packages from the cache:

```bash
dotnet nuget locals all --clear
```

### Check

```bash
dotnet --list-sdks       # no -preview / -rc entries
dotnet workload list     # empty until you reinstall maui-tizen 11.0.100.1
```

Then continue with [step 2](#2-install-the-net-11-ga-sdk).
