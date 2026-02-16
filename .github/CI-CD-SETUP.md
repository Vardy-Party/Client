# GitHub Actions CI/CD Documentation

This repository uses GitHub Actions to automate building, testing, and releasing the VardyParty application across multiple platforms.

## Workflows

### 1. **CI - Build & Test** (`.github/workflows/ci.yml`)
**Triggers:** On pull requests and pushes to `main`, `develop`, and `feature/**` branches

**Jobs:**
- **test** (Ubuntu): Runs unit tests for the core library
- **build-windows** (Windows): Builds Windows APPX package
- **build-android** (Ubuntu): Builds Android APK
- **build-ios** (macOS): Builds iOS IPA
- **build-macos** (macOS): Builds macOS app
- **build-linux** (Ubuntu): Builds Linux core library and prepares for future full app support

**Artifacts:**
- Platform-specific builds are uploaded as workflow artifacts for testing

### 2. **Release - Build & Package** (`.github/workflows/release.yml`)
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

### 3. **Code Quality** (`.github/workflows/code-quality.yml`)
**Triggers:** On pull requests and pushes to `main` and `develop`

**Checks:**
- Code style enforcement
- Code formatting verification

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
    └── VardyParty.Core.Tests/ # Unit tests
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

1. **Testing**: Unit tests run first and must pass before platform builds
2. **Branch Protection**: Consider requiring CI to pass before merging PRs
3. **Release Tags**: Use semantic versioning for releases (v1.0.0, v1.1.0, etc.)
4. **Monitoring**: Check workflow runs in the "Actions" tab for failures
5. **Secrets Management**: Store signing certificates in GitHub Secrets, not in the repository

## Troubleshooting

### Build Failures

Check the workflow logs in GitHub Actions:
1. Go to repository → Actions tab
2. Click on the failed workflow
3. Expand the failed job to see detailed error logs

### Platform-Specific Issues

- **Windows builds fail**: Verify Windows SDK version compatibility
- **Android builds fail**: Check Java version (17+), Android SDK setup
- **iOS builds fail**: Ensure macOS runner has latest Xcode, valid provisioning
- **macOS builds fail**: Similar to iOS; check signing certificates

## Local Development

To test builds locally before pushing:

```bash
# Restore dependencies
dotnet restore

# Build specific platform
dotnet build VardyParty/VardyParty.csproj -c Release -f net10.0-windows10.0.19041.0

# Run unit tests
dotnet test tests/VardyParty.Core.Tests/VardyParty.Core.Tests.csproj
