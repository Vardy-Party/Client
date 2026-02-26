# 🏗️ Version Management Architecture

## System Overview

```
┌────────────────────────────────────────────────────────────────────────────┐
│                          Version.props (Single Source)                     │
│  ┌──────────────────────────────────────────────────────────────────────┐ │
│  │  <ApplicationVersion>35</ApplicationVersion>                         │ │
│  │  <ApplicationDisplayVersion>1.7.2</ApplicationDisplayVersion>        │ │
│  │  <Product>VardyParty</Product>                                      │ │
│  │  <Company>Vardy Party</Company>                                     │ │
│  └──────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────┬────────────────────────────────────────┘
                                  │
                 ┌────────────────┼────────────────┐
                 │                │                │
    ┌────────────▼─────────┐ ┌────▼──────────┐ ┌──▼──────────────┐
    │   MAUI Project       │ │ Linux Project │ │ GitHub Actions  │
    │ VardyParty.csproj    │ │ .Linux.csproj │ │ (Workflows)     │
    │                      │ │               │ │                 │
    │ <Import Project=     │ │ <Import       │ │ • auto-incr.    │
    │ "..\Version.props"/> │ │ Project=      │ │ • bump-version  │
    │                      │ │ "..\Version   │ │ • build.yml     │
    │ ✓ MSBuild reads      │ │ .props" />    │ │                 │
    │   Version.props      │ │               │ │ ✓ Read from     │
    │   automatically      │ │ ✓ MSBuild     │ │   Version.props │
    │                      │ │   reads it    │ │   in workflows  │
    │ ✓ DLLs stamped:      │ │               │ │                 │
    │   1.7.2+35           │ │ ✓ DLLs        │ │ ✓ Auto-update   │
    │                      │ │   stamped:    │ │   Version.props │
    └──────────────────────┘ │   1.7.2+35    │ └──────────────────┘
                             │               │
                             └───────────────┘
                                    │
                    ┌───────────────┼───────────────┐
                    │               │               │
          ┌─────────▼────────┐ ┌────▼──────┐ ┌─────▼──────────┐
          │   Build (ci.yml) │ │ Package   │ │  Release       │
          │                  │ │(cd.yml)   │ │ (release.yml)  │
          │ ✓ Builds both    │ │           │ │                │
          │   projects       │ │ ✓ Creates:│ │ ✓ Triggered by │
          │ ✓ DLLs stamped   │ │   MSIX    │ │   git tags     │
          │ ✓ Tests run      │ │   APK     │ │ ✓ Version auto │
          │ ✓ Artifacts up   │ │   IPA     │ │   from props   │
          └──────────────────┘ │           │ └────────────────┘
                               │ ✓ Named:  │
                               │   v1.7.2  │
                               │   -b35    │
                               │           │
                               │ ✓ GitHub  │
                               │   Release │
                               │   created │
                               └───────────┘
```

## Data Flow: Normal Build

```
Developer Push
     │
     ├─→ ci.yml starts
     │    ├─ Clone repo (includes Version.props)
     │    ├─ Setup .NET 10
     │    ├─ Restore dependencies
     │    ├─ Build for Windows, Android, iOS
     │    │  └─ MSBuild reads Version.props automatically
     │    │     ├─ ApplicationVersion = 35
     │    │     └─ ApplicationDisplayVersion = 1.7.2
     │    ├─ DLLs compiled with version stamps
     │    ├─ Run unit tests
     │    └─ Upload artifacts
     │
     └─→ cd.yml starts (on main branch success)
          ├─ Download artifacts from ci.yml
          ├─ Extract version from Version.props
          │  ├─ DisplayVersion = 1.7.2
          │  └─ BuildVersion = 35
          ├─ Generate appsettings.json
          │  └─ Merge with Auth0/API secrets
          ├─ Build packages:
          │  ├─ Windows MSIX: VardyParty-windows-v1.7.2-b35.msix
          │  ├─ Android APK: VardyParty-android-v1.7.2-b35.apk
          │  └─ iOS IPA: VardyParty-ios-v1.7.2-b35.ipa
          ├─ Create GitHub Release: v1.7.2-b35
          └─ Upload assets
               │
               └─→ auto-increment-build-version runs
                    ├─ Read current ApplicationVersion: 35
                    ├─ Increment: 35 + 1 = 36
                    ├─ Update Version.props
                    ├─ Commit: "ci: auto-increment ApplicationVersion to 36"
                    └─ Push to main
                         └─ Next build will use ApplicationVersion=36
```

## Data Flow: Manual Version Bump

```
User Action: "Bump version to 1.8.0"
     │
     └─→ Trigger: bump-display-version workflow
          ├─ (Optional) Select bump type: major/minor/patch
          │
          ├─ Workflow starts:
          │  ├─ Read Version.props (current: 1.7.2)
          │  ├─ Calculate new version
          │  │  └─ If "minor" selected: 1.7.2 → 1.8.0
          │  ├─ Update Version.props: 1.7.2 → 1.8.0
          │  ├─ Commit: "ci: bump ApplicationDisplayVersion to 1.8.0"
          │  ├─ Push to main
          │  └─ Create git tag: v1.8.0
          │
          └─→ release.yml triggered by tag
               ├─ Clone repo (includes updated Version.props)
               ├─ Read Version.props (now 1.8.0)
               ├─ Build all platforms with 1.8.0
               ├─ DLLs stamped: 1.8.0+36 (current build number)
               ├─ Package for all platforms
               ├─ Create GitHub Release for v1.8.0
               └─ Upload artifacts as release assets
```

## Workflow Interaction Map

```
GitHub Events:
  │
  ├─ Push to any branch
  │   └─→ ci.yml (Build & Test)
  │       ├─ Reads: Version.props (auto-imported by MSBuild)
  │       ├─ On success: Uploads artifacts
  │       │
  │       └─→ (If main branch)
  │           └─→ cd.yml (Package & Release)
  │               ├─ Reads: Version.props
  │               ├─ Packages: MSIX, APK, IPA
  │               ├─ Creates: GitHub Release
  │               │
  │               └─→ auto-increment-build-version
  │                   ├─ Reads: Version.props
  │                   ├─ Updates: ApplicationVersion + 1
  │                   └─ Commits: "ci: auto-increment..."
  │
  ├─ Manual: Bump Display Version workflow
  │   └─→ bump-display-version
  │       ├─ Reads: Version.props
  │       ├─ Updates: ApplicationDisplayVersion
  │       ├─ Creates: git tag v{new_version}
  │       │
  │       └─→ release.yml (triggered by tag)
  │           ├─ Reads: Version.props (updated)
  │           ├─ Builds: All platforms
  │           └─ Creates: GitHub Release
  │
  └─ Manual: release.yml (on tag)
      └─→ Builds and packages
```

## Version Properties Inheritance

```
Version.props
     │
     ├─ ApplicationVersion = "35"
     │  └─ Used by: MSBuild → AssemblyFileVersion
     │             cd.yml → Release tagging
     │             GitHub Actions → Build counter
     │
     ├─ ApplicationDisplayVersion = "1.7.2"
     │  └─ Used by: MSBuild → AssemblyVersion
     │             cd.yml → Artifact naming
     │             GitHub Actions → Release naming
     │
     ├─ Product = "VardyParty"
     │  └─ Used by: MSBuild → Assembly info
     │
     ├─ Company = "Vardy Party"
     │  └─ Used by: MSBuild → DLL properties
     │
     └─ Copyright = "Copyright © ..."
         └─ Used by: MSBuild → DLL properties

        ↓ (MSBuild processes Version.props)

VardyParty.csproj + VardyParty.Linux.csproj
     │
     ├─ Both import: <Import Project="..\Version.props" />
     │
     └─ Both automatically get:
        ├─ $(ApplicationVersion) = 35
        ├─ $(ApplicationDisplayVersion) = 1.7.2
        ├─ $(Product) = VardyParty
        └─ etc.

        ↓ (Compilation with inherited properties)

Compiled Assemblies
     │
     ├─ VardyParty.dll (Windows/MAUI)
     │  └─ Assembly Version: 1.7.2.0
     │     File Version: 35
     │     Product: VardyParty
     │     Company: Vardy Party
     │
     └─ VardyParty (Linux/Avalonia)
        └─ Assembly Version: 1.7.2.0
           File Version: 35
           Product: VardyParty
           Company: Vardy Party
```

## Artifact Naming Convention

```
Before (Inconsistent):
  VardyParty.dll
  com.vardyparty.apk
  VardyParty.app

After (Consistent):
  VardyParty-windows-v1.7.2-b35.msix
  VardyParty-android-v1.7.2-b35.apk
  VardyParty-ios-v1.7.2-b35.ipa

Pattern: VardyParty-{platform}-v{DisplayVersion}-b{BuildVersion}.{ext}

Benefits:
  ✓ Version immediately visible in filename
  ✓ Consistent across all platforms
  ✓ Easy to sort by version
  ✓ No ambiguity about which build
```

## Git History with Version Management

```
(newest)
├─ 36d7f8a: "Merge pull request #123"
│   └─ Main branch (production)
│
├─ 25e6c4b: "ci: auto-increment ApplicationVersion to 37"
│   ├─ Author: GitHub Actions
│   ├─ Files: Version.props
│   └─ Automatic after cd.yml
│
├─ 14b5a3c: "ci: bump ApplicationDisplayVersion to 1.8.0"
│   ├─ Author: GitHub Actions
│   ├─ Files: Version.props
│   ├─ Tag: v1.8.0 ← Created by workflow
│   └─ Triggered by: bump-display-version workflow
│
├─ 03a4b2d: "Merge pull request #122"
│   └─ Previous release (v1.7.2)
│
└─ ...

(oldest)

Benefits:
  ✓ Version history is git history
  ✓ Every version has commit + tag
  ✓ Release notes auto-generated from commits
  ✓ Easy to rollback or cherry-pick versions
```

## File Organization

```
repository/
├─ Version.props ← Source of truth (NEW)
│
├─ VardyParty/
│  ├─ VardyParty.csproj (UPDATED)
│  │  ├─ <Import Project="..\Version.props" />
│  │  └─ <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
│  ├─ (other MAUI files)
│  └─ ...
│
├─ VardyParty.Linux/
│  ├─ VardyParty.Linux.csproj (UPDATED)
│  │  ├─ <Import Project="..\Version.props" />
│  │  └─ <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
│  ├─ (other Linux/Avalonia files)
│  └─ ...
│
├─ VardyParty.Core/
│  ├─ VardyParty.Core.csproj (unchanged)
│  └─ (shared library code)
│
├─ .github/workflows/
│  ├─ ci.yml (existing, works as-is)
│  ├─ cd.yml (existing, works as-is, can be updated)
│  ├─ release.yml (existing, works as-is)
│  ├─ auto-increment-build-version.yml (NEW)
│  ├─ bump-display-version.yml (NEW)
│  └─ build.yml (NEW)
│
└─ Documentation/
   ├─ VERSION_MANAGEMENT.md (NEW)
   ├─ VERSION_MANAGEMENT_QUICK_REFERENCE.md (NEW)
   ├─ CI_CD_INTEGRATION.md (NEW)
   ├─ BEFORE_AFTER.md (NEW)
   └─ IMPLEMENTATION_COMPLETE.md (NEW)
```

## State Machine: Version Progression

```
Feature Branch Development:
  ┌─────────────┐
  │ 1.7.2-b35   │ ← Developer working on feature
  │ (read-only) │
  └──────┬──────┘
         │ git push feature/my-feature
         ↓
    ci.yml runs
    (no version change)
         │
         └─ If passes → Ready for merge


Main Branch Merge:
  ┌──────────────┐
  │ 1.7.2-b35    │ ← Merged to main
  │ (before CD)  │
  └──────┬───────┘
         │
    ├─ ci.yml: ✓
    ├─ cd.yml: Package and release
    │
    └─→ After cd.yml completes:
         │
         └─→ auto-increment-build-version runs:
             │
             ├─ Read current: b35
             ├─ Calculate new: b36
             ├─ Write: b36 to Version.props
             ├─ Commit and push
             │
             └─ Result:
                ┌──────────────┐
                │ 1.7.2-b36    │ ← Ready for next build
                │ (after incr) │
                └──────────────┘


Manual Release (Semantic Bump):
  ┌──────────────┐
  │ 1.7.2-b36    │ ← Current state on main
  │              │
  └──────┬───────┘
         │
    User triggers: bump-display-version (minor)
         │
         ├─ Workflow starts
         ├─ Parse 1.7.2 → increment minor → 1.8.0
         ├─ Update Version.props: 1.7.2 → 1.8.0
         ├─ Create tag: v1.8.0
         │
         └─→ release.yml triggered
             │
             ├─ Build with 1.8.0
             ├─ Package all platforms
             ├─ Create GitHub Release v1.8.0
             │
             └─ Result:
                ┌──────────────┐
                │ 1.8.0-b37    │ ← Next build will use 1.8.0
                │ (bumped)     │   & auto-increment to b37
                └──────────────┘
```

## Command Flow Summary

```
Developer Perspective:

  FEATURE DEVELOPMENT (no version thought needed):
    $ git checkout -b feature/my-feature
    $ (make changes)
    $ git commit -m "feat: add new feature"
    $ git push origin feature/my-feature
    ✓ CI runs automatically, versions handled by system

  FEATURE MERGE TO MAIN:
    $ (open PR)
    $ (merge to main)
    ✓ CD runs automatically
    ✓ Packages created with versions
    ✓ Release created automatically
    ✓ Version incremented automatically

  MANUAL RELEASE:
    ✓ GitHub Actions UI
    ✓ Click: Bump Display Version on Main Merge
    ✓ Select: "minor"
    ✓ Version bumped to 1.8.0
    ✓ Release created automatically
    ✓ No command line needed
```

## Key Design Principles

```
┌─────────────────────────────────────────────────────────┐
│ CENTRALIZATION                                          │
│ ├─ Single Version.props file (not 2 .csproj files)     │
│ └─ All projects import same source                     │
├─────────────────────────────────────────────────────────┤
│ AUTOMATION                                              │
│ ├─ No manual version management needed                 │
│ ├─ Increments triggered automatically                  │
│ └─ Bumps triggered on-demand via UI                   │
├─────────────────────────────────────────────────────────┤
│ AUDITABILITY                                            │
│ ├─ Every version change committed to git               │
│ ├─ Git tags mark releases                              │
│ └─ Full history visible in git log                     │
├─────────────────────────────────────────────────────────┤
│ CONSISTENCY                                             │
│ ├─ Both projects always in sync                        │
│ ├─ DLLs stamped consistently                           │
│ └─ Artifacts named consistently                        │
├─────────────────────────────────────────────────────────┤
│ SIMPLICITY                                              │
│ ├─ No configuration files to maintain                  │
│ ├─ Works with existing CI/CD (ci.yml, cd.yml)         │
│ └─ Minimal learning curve                              │
└─────────────────────────────────────────────────────────┘
```

---

**This architecture ensures version management is:**
- ✅ Centralized (single source)
- ✅ Automated (hands-off)
- ✅ Auditable (git history)
- ✅ Consistent (both projects sync)
- ✅ Integrated (works with CI/CD)
