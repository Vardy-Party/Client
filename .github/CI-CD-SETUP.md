# GitHub Actions CI/CD Documentation

This repository uses GitHub Actions to automate building, testing, and releasing the VardyParty application across multiple platforms.

## Workflow Architecture

### Three Separate Workflows

1. **CI** - Continuous Integration (run on all branches, builds all platforms)
2. **CD** - Continuous Deployment (run on main/feature/github-builds after CI passes, packages artifacts into installers)
3. **Release** - Version releases (run on version tags, creates GitHub releases with packages)

## Artifact Flow

```
CI Workflow (Build All Platforms)
├─ test: Unit Tests (Ubuntu)
├─ code-quality: StyleCop & Formatting (Ubuntu)
├─ build-windows: Publish app → upload build-windows artifact
├─ build-android: Publish APK → upload build-android artifact
├─ build-ios: Publish IPA → upload build-ios artifact
├─ build-macos: Publish app → upload build-macos artifact
└─ build-linux: Publish binaries → upload build-linux-x64, build-linux-arm64 artifacts
       ↓
CD Workflow (Package Artifacts)
├─ package-windows: Download build-windows → ZIP → upload windows-installer
├─ package-android: Download build-android → upload android-installer
├─ package-ios: Download build-ios → ZIP → upload ios-installer
├─ package-macos: Download build-macos → ZIP → upload macos-installer
├─ package-linux-x64: Download build-linux-x64 → TAR.GZ → upload linux-x64-installer
└─ package-linux-arm64: Download build-linux-arm64 → TAR.GZ → upload linux-arm64-installer
```

## Workflows

### 1. **CI - Build & Test** (`.github/workflows/ci.yml`)
**Triggers:** On pull requests and pushes to `main`, `develop`, and `feature/github-builds`

**Jobs:**
- **test** (Ubuntu): Runs unit tests for the core library
- **code-quality** (Ubuntu): Runs StyleCop and code formatting checks
- **build-windows** (Windows): Builds and publishes Windows app
- **build-android** (Ubuntu): Builds and publishes Android APK
- **build-ios** (macOS): Builds and publishes iOS IPA
- **build-macos** (macOS): Builds and publishes macOS app
- **build-linux** (Ubuntu): Builds and publishes Linux binaries (x64 and ARM64)

**Artifacts Uploaded:**
- `build-windows` - Windows application files
- `build-android` - Android APK files
- `build-ios` - iOS IPA files
- `build-macos` - macOS application files
- `build-linux-x64` - Linux x64 binaries
- `build-linux-arm64` - Linux ARM64 binaries

**Retention:** 5 days

### 2. **CD - Package & Release** (`.github/workflows/cd.yml`)
**Triggers:** After CI workflow completes successfully, on `main` and `feature/github-builds` branches only

**Jobs:**
- **package-windows**: Downloads `build-windows` artifact, creates ZIP installer, uploads `windows-installer`
- **package-android**: Downloads `build-android` artifact, uploads `android-installer`
- **package-ios**: Downloads `build-ios` artifact, creates ZIP installer, uploads `ios-installer`
- **package-macos**: Downloads `build-macos` artifact, creates ZIP installer, uploads `macos-installer`
- **package-linux-x64**: Downloads `build-linux-x64` artifact, creates TAR.GZ installer, uploads `linux-x64-installer`
- **package-linux-arm64**: Downloads `build-linux-arm64` artifact, creates TAR.GZ installer, uploads `linux-arm64-installer`

**Purpose:** Package CI build artifacts into distributable installers

**Artifacts Uploaded:**
- `windows-installer` - Packaged Windows installer
- `android-installer` - Packaged Android APK
- `ios-installer` - Packaged iOS installer
- `macos-installer` - Packaged macOS installer
- `linux-x64-installer` - Packaged Linux x64 installer
- `linux-arm64-installer` - Packaged Linux ARM64 installer

**Retention:** 30 days

**Dependency:** Only runs if CI workflow passes (`if: github.event.workflow_run.conclusion == 'success'`)

### 3. **Release - Build & Package** (`.github/workflows/release.yml`)
**Triggers:** On version tags matching `v*` pattern (e.g., `v1.0.0`)

**Jobs:**
- Creates a GitHub Release
- Builds and packages installers for all platforms
- Uploads packages as release assets

**Usage:**
```bash
git tag v1.0.0
git push origin v1.0.0
```

Installers will be automatically created and attached to the GitHub Release.

## Platform Requirements

### Windows (APPX)
- Builds on: `windows-latest`
- Framework: `net10.0-windows10.0.19041.0`
- Output: Self-contained Windows package

### Android (APK)
- Builds on: `ubuntu-latest`
- Framework: `net10.0-android`
- Requirements: Java 17
- Output: Multi-architecture APK (arm, arm64, x86, x86_64)

### iOS (IPA)
- Builds on: `macos-14`
- Framework: `net10.0-ios`
- Output: iOS app archive

### macOS
- Builds on: `macos-14`
- Framework: `net10.0-maccatalyst`
- Output: macOS application

### Linux
- Builds on: `ubuntu-latest`
- Frameworks: `net10.0` (core library + future app)
- Architectures: x64, ARM64 (self-contained)
- Output: Compressed archives (.tar.gz)
- Status: Preparing for future full Linux app support

## Project Structure

```
VardyParty/
├── VardyParty.csproj          # Main MAUI app (multi-platform)
├── VardyParty.Core/
│   └── VardyParty.Core.csproj # Core library
└── tests/
    └── VardyParty.Tests/ # Unit tests
```

## Notes

### Signing & Certificate Requirements

For production releases, you may need to configure:

**iOS:**
- Add provisioning profiles to Xcode Cloud or GitHub secrets
- Configure iOS signing certificate in the build process

**Android:**
- Add keystore file to secrets
- Configure signing in the APK publish step

**Windows:**
- For Store releases, configure signing certificate and publisher info

**macOS:**
- Configure signing certificate for App Store distribution

### Modifying Build Configuration

To adjust build parameters, edit the relevant workflow YAML:
- Platform-specific frameworks
- Build configurations
- Output locations
- Artifact naming

### Workload Restoration

All workflows automatically run `dotnet workload restore` to install required .NET MAUI workloads. This includes:
- `maui` - Core MAUI framework
- `maui-android` - Android platform support
- `maui-ios` - iOS platform support
- `maui-maccatalyst` - macOS platform support
- Plus all dependencies

This step ensures the GitHub Actions runners have all necessary components to build for each platform.

### Test Artifacts

During CI builds, all platform builds are available as workflow artifacts:
1. Navigate to the workflow run on GitHub
2. Scroll to "Artifacts" section
3. Download the desired platform build

## Best Practices

1. **CI First**: Unit tests and code quality checks run on every push/PR
2. **CD Gated**: Installers only build if CI passes (`workflow_run` dependency)
3. **Branch-Specific CD**: CD only runs on `main` (production) and `feature/github-builds` (development)
4. **Release Tags**: Use semantic versioning for releases (v1.0.0, v1.1.0, etc.)
5. **Monitoring**: Check workflow runs in the "Actions" tab for failures
6. **Secrets Management**: Store signing certificates in GitHub Secrets, not in the repository

## Workflow Flow

```
Every push → CI runs (test + code-quality + build all platforms)
              ├─ Build artifacts uploaded (5-day retention)
              ├─ If on main/feature/github-builds AND CI passes → CD runs (package artifacts)
              │  └─ Installer artifacts uploaded (30-day retention)
              └─ If tag v* pushed → Release runs (create release + upload assets)
```

## Local Testing

To test the pipeline locally before pushing:

```bash
# Run tests locally
dotnet test tests/VardyParty.Tests/VardyParty.Tests.csproj --configuration Release

# Check formatting
dotnet format --verify-no-changes

# Build for a specific platform
dotnet workload restore
dotnet publish VardyParty/VardyParty.csproj -c Release -f net10.0-windows10.0.19041.0 -o ./artifacts/windows
```

## Accessing Build Artifacts

During development on `feature/github-builds`:

1. Go to GitHub Actions tab
2. Find the CI workflow run you want
3. Scroll to "Artifacts" section
4. Download either:
   - **build-{platform}** artifacts from CI (raw builds)
   - **{platform}-installer** artifacts from CD (packaged installers)

Note: Artifacts are retained for:
- **CI builds**: 5 days (to save storage)
- **CD packages**: 30 days (ready-to-use installers)
