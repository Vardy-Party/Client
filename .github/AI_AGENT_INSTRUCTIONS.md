# VardyParty - AI Agent Instructions

Welcome, AI Agent! Follow these guidelines for the VardyParty Client repo.

## Project Context

**Project Name:** VardyParty  
**Tech Stack:** .NET 11 (preview), MAUI (Android/iOS/Mac Catalyst/Windows), Avalonia MAUI backend on Linux (`VardyParty.Desktop`), native players (Exo / WinUI / AVPlayer / LibVLC)  
**Repository:** https://github.com/Vardy-Party/Client  
**Purpose:** Multi-platform live football streaming with score aggregation  

**Docs entry point:** [docs/INDEX.md](../docs/INDEX.md)  
**Architecture:** [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md) · [homepage-maui-avalonia.md](../docs/architecture/homepage-maui-avalonia.md)  
**Versioning:** [docs/VERSION_MANAGEMENT.md](../docs/VERSION_MANAGEMENT.md) — do not hand-edit display semver on feature branches  
**PR #74 review:** [docs/reviews/PR-74-flagship-review.md](../docs/reviews/PR-74-flagship-review.md)

## Primary objectives

1. Understand the architecture — one shared `HomeUi` XAML; Linux draws it via Avalonia; each OS keeps its own native player
2. Follow coding standards — see `.github/copilot-instructions.md`
3. Respect versioning automation — `Version.props` is workflow-managed for display bumps
4. Keep `/docs` living — prefer INDEX-linked docs; do not resurrect deleted ceremony files

## Repository structure

```
docs/
├─ INDEX.md
├─ ARCHITECTURE.md
├─ architecture/homepage-maui-avalonia.md
├─ STREAM_PLAYBACK_RULES.md / STREAM_HEALTH_PROTOCOL.md
├─ LINUX_SUPPORT.md / LOCAL_ANDROID_BUILD.md / WINDOWS_INSTALL.md
├─ VERSION_MANAGEMENT.md
├─ agent-playbook-merge-client-pr-v2.md
└─ reviews/PR-74-flagship-review.md

.github/
├─ copilot-instructions.md
├─ CI-CD-SETUP.md
└─ workflows/ (ci.yml, cd.yml, release.yml, bump-display-version.yml, …)

VardyParty/           ← MAUI head (Android/iOS/Mac Catalyst/Windows)
VardyParty.HomeUi/    ← shared XAML homepage
VardyParty.Desktop/   ← Linux/WSL head (Avalonia draw + LibVLC)
VardyParty.*/         ← Kernel, Ports, Auth, Catalog, Streaming, Playback, Presentation, Hosting
tests/
Version.props         ← single version source
```


## 🔑 Key Files You Need to Know

### Version Management
- **`Version.props`** - Single source of truth for versions (ApplicationVersion, ApplicationDisplayVersion)
  - ⚠️ **DO NOT EDIT MANUALLY** during development
  - GitHub Actions workflows manage it automatically
  - VardyParty, VardyParty.HomeUi and VardyParty.Desktop import this file

### Project Files
- **`VardyParty/VardyParty.csproj`** - MAUI app (Windows/Android/iOS/macOS)
  - Contains: `<Import Project="..\Version.props" />`
  - Targets: net11.0-android, net11.0-ios, net11.0-maccatalyst, net11.0-windows

- **`VardyParty.Desktop/VardyParty.Desktop.csproj`** - Linux desktop head (MAUI-Avalonia)
  - Contains: `<Import Project="..\Version.props" />`
  - Target: net11.0 with linux-x64, linux-arm64 runtimes

### Documentation
- **`docs/INDEX.md`** - Documentation index and quick start
- **`docs/VERSION_MANAGEMENT.md`** - Complete version management reference
- **`.github/CI-CD-SETUP.md`** - How versions integrate with CI/CD
- **`docs/ARCHITECTURE.md`** - System architecture and data flows

### CI/CD Workflows
- **`.github/workflows/ci.yml`** - Build & Test (existing)
- **`.github/workflows/cd.yml`** - Package & Release (existing)
- **`.github/workflows/auto-increment-build-version.yml`** - Auto-increment build counter
- **`.github/workflows/bump-display-version.yml`** - Manual semantic versioning

## 🛠️ Development Workflow

### Setting Up Local Environment

```bash
# Clone repository
git clone https://github.com/Vardy-Party/Client.git
cd VardyParty-Client

# Restore dependencies
dotnet restore

# Build solution
dotnet build

# Run tests
dotnet test

# Build MAUI app
dotnet build VardyParty/VardyParty.csproj -c Release -f net11.0-android

# Build Linux desktop head
dotnet build VardyParty.Desktop/VardyParty.Desktop.csproj -c Release
```

### Version Management (Automated)

**Check current versions:**
```bash
grep ApplicationDisplayVersion Version.props  # Display version (e.g., 1.7.2)
grep ApplicationVersion Version.props         # Build counter (e.g., 35)
```

**DO NOT manually edit Version.props. Instead:**
- **For development builds:** Push to feature branch → CI auto-handles versions
- **For releases:** Trigger GitHub Actions > "Bump Display Version" workflow → Select major/minor/patch

### Creating Features

```bash
# Create feature branch
git checkout -b feature/your-feature-name

# Make changes (don't touch Version.props)
# Commit changes
git add .
git commit -m "feat: add new feature"

# Push and create PR
git push origin feature/your-feature-name
```

The CI pipeline will:
1. Run tests automatically
2. Build for all platforms
3. Stamp DLLs with version from Version.props
4. Upload artifacts

### Creating Releases

**For automated releases:**
1. Go to GitHub Actions
2. Click "Bump Display Version on Main Merge"
3. Select bump type: major, minor, or patch
4. Workflow will:
   - Update Version.props
   - Create git tag
   - Trigger release.yml
   - Create GitHub Release with all artifacts

## 📋 Coding Standards

**See `.github/copilot-instructions.md` for detailed team standards including:**
- Naming conventions
- Code style
- Comment guidelines
- Architecture patterns
- Testing requirements

**Quick Rules:**
- Use meaningful variable/method names
- Follow .NET naming conventions (PascalCase for public, camelCase for private)
- Comment complex logic, not obvious code
- Write unit tests for new features
- Keep methods focused and small

## 🔄 CI/CD Pipeline

### Build Process (ci.yml)
```
Feature/Main Push → ci.yml starts
  ├─ Restore dependencies
  ├─ Build all platforms (Windows, Android, iOS, Linux)
  ├─ Run unit tests
  ├─ DLLs stamped with version from Version.props
  └─ Upload build artifacts
```

### Packaging Process (cd.yml) - On Main Branch Success
```
ci.yml success on main → cd.yml starts
  ├─ Download artifacts
  ├─ Extract version from Version.props
  ├─ Generate appsettings.json (merges with Auth0/API secrets)
  ├─ Package:
  │  ├─ Windows MSIX
  │  ├─ Android APK
  │  └─ iOS IPA
  ├─ Create GitHub Release
  └─ Upload artifacts
    └─ auto-increment-build-version runs after
       ├─ Increments ApplicationVersion
       └─ Commits back to main
```

### Release Process (release.yml) - On Tag
```
Tag created (v1.8.0) → release.yml starts
  ├─ Build with tagged version
  ├─ Package all platforms
  └─ Create/update GitHub Release
```

## ⚠️ Important Conventions

### DO ✅

- ✅ Run tests locally before pushing
- ✅ Create feature branches for new work
- ✅ Write descriptive commit messages
- ✅ Update documentation when changing architecture
- ✅ Follow existing code patterns
- ✅ Add tests for new features
- ✅ Review CI logs if build fails
- ✅ Use semantic versioning (major.minor.patch)

### DON'T ❌

- ❌ Push directly to main
- ❌ Edit Version.props manually during development
- ❌ Commit code that breaks tests
- ❌ Use version numbers in code (use Version.props instead)
- ❌ Make large changes without documenting
- ❌ Ignore CI pipeline failures
- ❌ Merge PRs with test failures
- ❌ Mix multiple features in one PR

## 📚 Documentation

**All documentation is in `/docs` folder.** Start with:

1. **`docs/INDEX.md`** - Overview and navigation (5 min)
2. **`docs/ARCHITECTURE.md`** - What changed (10 min)
3. **`docs/VERSION_MANAGEMENT.md`** - Quick answers (5 min)
4. **`docs/ARCHITECTURE.md`** - System design (20 min)
5. **`.github/CI-CD-SETUP.md`** - CI/CD details (15 min)
6. **`docs/VERSION_MANAGEMENT.md`** - Complete reference (30 min)

## 🐛 Common Tasks

### Task: Add a new feature
```
1. Create feature branch: git checkout -b feature/my-feature
2. Make changes in appropriate project (VardyParty, VardyParty.HomeUi, VardyParty.Desktop, or the domain libraries)
3. Add/update tests
4. Commit: git commit -m "feat: add my feature"
5. Push: git push origin feature/my-feature
6. Create PR and wait for CI to pass
7. Merge when approved
```

### Task: Fix a bug
```
1. Create bug branch: git checkout -b fix/bug-description
2. Write failing test first (TDD)
3. Fix the code
4. Verify test passes
5. Commit: git commit -m "fix: describe the fix"
6. Push and create PR
```

### Task: Release a new version
```
1. Ensure main branch is up-to-date
2. Go to GitHub Actions
3. Click "Bump Display Version on Main Merge"
4. Select: major / minor / patch
5. Workflow handles: version update, git tag, release creation
6. Done! Release is created automatically
```

### Task: Check version in DLL
```powershell
# Windows PowerShell
$asm = [System.Reflection.AssemblyName]::GetAssemblyName("path\to\VardyParty.dll")
$asm.Version  # Should show: 1.7.2.0
```

### Task: Verify version stamping
```bash
# Build and check
dotnet build VardyParty/VardyParty.csproj -c Release
dotnet build VardyParty.Desktop/VardyParty.Desktop.csproj -c Release
# Right-click DLL > Properties > Details tab to see version
```

## 🔍 Debugging Guidance

### Build fails: "Version.props not found"
- Ensure `Version.props` is in solution root (not nested)
- Check: `<Import Project="..\Version.props" />` in .csproj
- Verify path is relative to project file location

### Version not showing in artifacts
- Check: Did CI actually run? Check GitHub Actions logs
- Verify: Version.props has both ApplicationVersion and ApplicationDisplayVersion
- Confirm: `<Import Project="..\Version.props" />` is in both .csproj files

### Artifact naming wrong
- Check: cd.yml version extraction step is correct
- Verify: Version.props matches what cd.yml is reading
- Optional: Update cd.yml to read from Version.props (see CI_CD_INTEGRATION.md)

### Tests failing
- Run locally first: `dotnet test`
- Check output for specific failure
- Look at git diff for unintended changes
- Review CI logs for platform-specific issues

## 📞 Getting Help

1. **Quick answers:** Check `docs/VERSION_MANAGEMENT.md`
2. **Version questions:** Read `docs/VERSION_MANAGEMENT.md`
3. **CI/CD questions:** Read `.github/CI-CD-SETUP.md`
4. **Architecture questions:** Read `docs/ARCHITECTURE.md`
5. **Code standards:** Read `.github/copilot-instructions.md`
6. **GitHub Issues:** Search existing issues or create new one

## 🚀 Quick Start for New AI Agents

1. **Clone repo** and read `docs/INDEX.md`
2. **Understand structure:** Review `/docs` and `.github/`
3. **Check project type:** Multi-platform (.NET 11)
4. **Know version system:** Single `Version.props` file (don't edit manually)
5. **Follow CI/CD:** Understand 3-tier pipeline (ci.yml → cd.yml → release.yml)
6. **Code standards:** Read `.github/copilot-instructions.md`
7. **Make changes:** Create feature branch, push, let CI handle versions
8. **Document:** Update `/docs` if architectural changes

## ✅ Before You Submit Code

- [ ] Tests pass locally: `dotnet test`
- [ ] No manual Version.props edits
- [ ] Changes follow coding standards
- [ ] Relevant docs updated
- [ ] Commit messages are descriptive
- [ ] PR title is clear
- [ ] No hardcoded versions in code

## 📊 Quick Reference

| Question | Answer |
|----------|--------|
| Where are docs? | `/docs` folder |
| How to check version? | `grep ApplicationDisplayVersion Version.props` |
| How to bump version? | GitHub Actions > Bump Display Version |
| Where are workflows? | `.github/workflows/` |
| How to run tests? | `dotnet test` |
| How to build MAUI? | `dotnet build VardyParty/VardyParty.csproj` |
| How to build Linux? | `dotnet build VardyParty.Desktop/VardyParty.Desktop.csproj` |
| Who manages versions? | GitHub Actions (don't edit manually) |

---

**Last Updated:** 2024
**Status:** ✅ Active
**Maintained by:** DevOps & Platform Engineering
**For AI Agents:** This guide is yours. Use it to make better decisions about the codebase.
