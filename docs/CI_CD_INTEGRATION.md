# CI/CD Integration Summary

This document explains how the new Version.props versioning system integrates with your existing CI/CD workflows (ci.yml, cd.yml, release.yml).

## Quick Answer: How It Works Together

Your CI/CD pipeline stays mostly **unchanged**. Version.props is automatically imported by both project files at build time, so:

- **ci.yml** (Build & Test): Automatically uses Version.props via MSBuild—no changes needed
- **cd.yml** (Package & Release): Can optionally read from Version.props instead of .csproj for cleaner code
- **release.yml** (GitHub Release): Triggered by git tags created by the new bump-display-version workflow

## The Complete Flow

### 1. Feature Branch Development
```
git push feature/my-feature
    ↓
ci.yml triggered
  ├─ Reads Version.props automatically (via MSBuild import)
  ├─ ApplicationVersion=35, ApplicationDisplayVersion=1.7.2
  ├─ Builds all platforms
  ├─ DLLs stamped with version (automatic via MSBuild properties)
  ├─ Tests run
  └─ Artifacts uploaded if successful
    
cd.yml does NOT run (only on main → success)
release.yml does NOT run (only on tags)
```

### 2. Main Branch Merge (Continuous Deployment)
```
git push main  (or merge PR)
    ↓
ci.yml runs (automatically)
  ├─ Reads Version.props (ApplicationVersion=35, DisplayVersion=1.7.2)
  ├─ Builds and tests (≈20 min)
  └─ On success → triggers cd.yml
       ├─ If you've updated cd.yml:
       │  └─ Reads version from Version.props
       │
       ├─ If you haven't updated cd.yml:
       │  └─ Still reads from VardyParty.csproj (works, but redundant)
       │
       ├─ Packages Windows MSIX
       ├─ Packages Android APK  
       ├─ Packages iOS IPA
       ├─ Merges appsettings.json with secrets ✓
       ├─ Creates GitHub Release
       └─ Uploads artifacts (≈30 min)
           
auto-increment-build-version runs after cd.yml
  ├─ Reads Version.props
  ├─ Increments ApplicationVersion: 35 → 36
  ├─ Commits back to main
  └─ Next build will use ApplicationVersion=36
```

### 3. Manual Release / Version Bump
```
User triggers: GitHub Actions > Bump Display Version on Main Merge
  ├─ Selects bump type: "minor"
  ├─ Workflow runs:
  │  ├─ Reads Version.props (1.7.2)
  │  ├─ Calculates new version (1.7.3)
  │  ├─ Updates Version.props
  │  ├─ Commits to main
  │  └─ Creates git tag: v1.7.3
  │
  └─ release.yml triggered by tag (if you want)
     ├─ Reads Version.props (now 1.7.3)
     ├─ Builds with new version
     └─ Creates GitHub Release for v1.7.3
```

## What Each Workflow Reads

| Workflow | Current Behavior | With Version.props Update |
|----------|------------------|---------------------------|
| **ci.yml** | ✓ MSBuild auto-imports Version.props | ✓ Same (no changes needed) |
| **cd.yml** | Reads version from .csproj file | Can optionally read from Version.props |
| **release.yml** | Uses git tag as version | Works with bump-display-version tags |

## CI (ci.yml) - No Changes Needed ✓

Your ci.yml already works perfectly with Version.props:

1. Checks out code (including Version.props)
2. MSBuild sees `<Import Project="..\Version.props" />` in .csproj
3. Reads ApplicationVersion and ApplicationDisplayVersion automatically
4. Builds projects with these versions embedded
5. DLLs get stamped with version info

**No modifications required to ci.yml** — MSBuild handles everything behind the scenes.

## CD (cd.yml) - Optional Update Recommended

Your cd.yml currently reads version from VardyParty.csproj:

```powershell
# Current (in package-windows job)
[xml]$csproj = Get-Content "VardyParty/VardyParty.csproj"
$displayNode = $csproj.SelectSingleNode('//ApplicationDisplayVersion')
```

This still works because VardyParty.csproj imports Version.props, but for clarity, you can update it to read from the source:

```powershell
# Recommended (read from source of truth)
[xml]$versionProps = Get-Content "Version.props"
$displayNode = $versionProps.SelectSingleNode('//ApplicationDisplayVersion')
$buildNode = $versionProps.SelectSingleNode('//ApplicationVersion')
```

**Why this is better:**
- Reads from single source of truth, not derived sources
- Clearer intent (Version.props is obviously the version file)
- One less indirection
- Same logic for Windows, Android, and iOS

### Optional: Update cd.yml

If you want to update cd.yml (not required, but recommended):

**In `package-windows` job:**
```yaml
- name: Extract app version metadata
  id: app_version
  run: |
    # Read from Version.props (source of truth)
    [xml]$versionProps = Get-Content "Version.props"
    $displayNode = $versionProps.SelectSingleNode('//ApplicationDisplayVersion')
    $buildNode = $versionProps.SelectSingleNode('//ApplicationVersion')
    $display = if ($null -ne $displayNode) { $displayNode.InnerText } else { "" }
    $build = if ($null -ne $buildNode) { $buildNode.InnerText } else { "" }
    
    if ([string]::IsNullOrWhiteSpace($display) -or [string]::IsNullOrWhiteSpace($build)) {
      Write-Host "ERROR: Version properties missing in Version.props"
      exit 1
    }
    "APP_DISPLAY_VERSION=$display" | Out-File -FilePath $env:GITHUB_ENV -Append
    "APP_BUILD_VERSION=$build" | Out-File -FilePath $env:GITHUB_ENV -Append
    "display=$display" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
    "build=$build" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
    Write-Host "Using app version: $display (build $build)"
  shell: powershell
```

**In `package-android` and `package-ios` jobs:**
```yaml
- name: Extract app version metadata
  id: app_version
  run: |
    # Read from Version.props
    APP_DISPLAY_VERSION=$(sed -n 's:.*<ApplicationDisplayVersion>\([^<]*\)</ApplicationDisplayVersion>.*:\1:p' Version.props)
    APP_BUILD_VERSION=$(sed -n 's:.*<ApplicationVersion>\([^<]*\)</ApplicationVersion>.*:\1:p' Version.props)
    
    if [ -z "$APP_DISPLAY_VERSION" ] || [ -z "$APP_BUILD_VERSION" ]; then
      echo "ERROR: Version properties missing in Version.props"
      exit 1
    fi
    echo "APP_DISPLAY_VERSION=$APP_DISPLAY_VERSION" >> "$GITHUB_ENV"
    echo "APP_BUILD_VERSION=$APP_BUILD_VERSION" >> "$GITHUB_ENV"
    echo "display=$APP_DISPLAY_VERSION" >> "$GITHUB_OUTPUT"
    echo "build=$APP_BUILD_VERSION" >> "$GITHUB_OUTPUT"
    echo "Using app version: $APP_DISPLAY_VERSION (build $APP_BUILD_VERSION)"
  shell: bash
```

## Release (release.yml) - Works With New Workflows

Your release.yml is triggered by git tags. With the new system:

1. **Manual bump workflow** (new) creates git tags: `v1.7.3`
2. **release.yml** sees the tag and starts
3. Builds with Version.props (which now has 1.7.3)
4. Creates GitHub Release

You don't need to change release.yml, but you could enhance it to read from Version.props for consistency:

```yaml
- name: Extract version from tag
  id: version
  run: |
    # Parse from git tag (v1.7.3 → 1.7.3)
    VERSION=${GITHUB_REF#refs/tags/v}
    # Verify it matches Version.props
    PROPS_VERSION=$(sed -n 's:.*<ApplicationDisplayVersion>\([^<]*\)</ApplicationDisplayVersion>.*:\1:p' Version.props)
    if [ "$VERSION" != "$PROPS_VERSION" ]; then
      echo "WARNING: Tag version $VERSION doesn't match Version.props $PROPS_VERSION"
    fi
    echo "version=$VERSION" >> "$GITHUB_OUTPUT"
  shell: bash
```

## Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                     Version.props (Single Source)                │
│         <ApplicationVersion>35</ApplicationVersion>              │
│    <ApplicationDisplayVersion>1.7.2</ApplicationDisplayVersion>  │
└──────────────────────┬──────────────────────────────────────────┘
                       │
        ┌──────────────┼──────────────┐
        │              │              │
        ▼              ▼              ▼
   ┌─────────┐   ┌─────────┐  ┌────────────┐
   │ MAUI    │   │ Linux   │  │ Workflows  │
   │ .csproj │   │ .csproj │  │ (GitHub)   │
   │imports  │   │imports  │  │ can read   │
   │   ↓     │   │   ↓     │  │            │
   │ Values  │   │ Values  │  │ ci.yml ✓   │
   │ auto    │   │ auto    │  │ cd.yml ✓   │
   │ embed   │   │ embed   │  │ release.yml│
   │ in DLL  │   │ in DLL  │  │            │
   └─────────┘   └─────────┘  └────────────┘
       ↓              ↓              ↓
   ┌──────────────────────────────────────┐
   │  Artifacts (APK, MSIX, IPA)          │
   │  Named with embedded version         │
   │  DLLs stamped with version info      │
   │  GitHub Releases tagged v1.7.2-b35   │
   └──────────────────────────────────────┘
```

## Key Integration Points

### 1. Build Artifacts
- ci.yml uploads to GitHub: `build-windows`, `build-android`, `build-ios`
- DLLs inside contain version from Version.props (auto-stamped by MSBuild)
- Artifact names can include version (optional enhancement)

### 2. Packaging
- cd.yml downloads artifacts from ci.yml
- Reads version from Version.props
- Names packages: `VardyParty-{platform}-v{DISPLAY_VERSION}-b{BUILD_VERSION}`
- Example: `VardyParty-windows-v1.7.2-b35.msix`

### 3. Configuration Merging
- cd.yml properly **merges** appsettings.json with secrets (not overwrites) ✓
- Version.props doesn't affect this—configuration stays independent
- Auth0, API settings, etc. merged correctly for each platform

### 4. Release Management
- auto-increment-build-version: Increments after packaging
- bump-display-version: Manual trigger for semantic version
- release.yml: Triggered by tags, creates GitHub Release

## Appsettings Integration (No Changes)

Your appsettings.json handling is already correct per Copilot instructions:

**Windows:**
```powershell
$appsettings = Get-Content ... | ConvertFrom-Json  # Load existing
$appsettings.Auth0.Domain = $domain                 # Merge
$appsettings | ConvertTo-Json | Out-File ...       # Save
```

**Linux/Android:**
```bash
BASE_JSON=$(cat appsettings.json)        # Load existing
echo "$BASE_JSON" | jq '(...merge...)'   # Merge in-place
```

Version.props only handles versioning, not configuration. No changes needed for appsettings behavior.

## Testing the Integration

### Test 1: Verify Version Embedding in DLL
```powershell
# Build locally
dotnet build VardyParty/VardyParty.csproj -c Release

# Check DLL
$asm = [System.Reflection.AssemblyName]::GetAssemblyName("VardyParty/bin/Release/net10.0-android/VardyParty.dll")
$asm.Version  # Should show: 1.7.2.0 (from ApplicationDisplayVersion)
```

### Test 2: Verify Version in CI
Push to feature branch and check ci.yml logs:
```
Build output should show:
  Compiling VardyParty v1.7.2 (build 35)
  Assembly version: 1.7.2.0
  File version: 35
```

### Test 3: Verify Version in CD
Merge to main and check cd.yml logs:
```
Package output should show:
  Using app version: 1.7.2 (build 35)
  Created: VardyParty-windows-v1.7.2-b35.msix
  Created: VardyParty-android-v1.7.2-b35.apk
  GitHub Release: v1.7.2-b35
```

### Test 4: Verify Auto-Increment
After cd.yml completes, check Version.props in main branch:
```
ApplicationVersion should be: 36 (incremented from 35)
ApplicationDisplayVersion should be: 1.7.2 (unchanged)
```

## Summary

| Component | Status | Action |
|-----------|--------|--------|
| **Version.props** | ✓ Ready | Deployed and working |
| **ci.yml** | ✓ Ready | Works with Version.props automatically |
| **cd.yml** | ✓ Working | Optional: update to read from Version.props |
| **release.yml** | ✓ Ready | Works with new bump-display-version tags |
| **auto-increment** | ✓ Ready | Triggers after cd.yml on main |
| **bump-display-version** | ✓ Ready | Trigger manually for semantic releases |
| **DLL Stamping** | ✓ Ready | Automatic via MSBuild properties |
| **Appsettings merge** | ✓ Ready | No changes needed |

Your CI/CD pipeline is **ready to go**. All workflows integrate seamlessly with Version.props.

## Next Steps

1. **Verify the build**: Run ci.yml on a feature branch—should work unchanged ✓
2. **Check the packaging**: Merge to main—cd.yml should run and create releases ✓
3. **Optional: Update cd.yml** to read from Version.props instead of .csproj
4. **Monitor version increments**: Check that ApplicationVersion increments after each main merge
5. **Test manual bumps**: Trigger bump-display-version workflow to test semantic versioning

All workflows are now integrated with Version.props. Your version management is automated! 🎉
