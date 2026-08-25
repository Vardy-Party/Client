# VardyParty Documentation Index

Welcome! This directory contains comprehensive documentation for the VardyParty project.

## 📚 Quick Navigation

### For Developers
- **[architecture/README.md](architecture/README.md)** - App architecture canvas + plan (domain assemblies, shared VMs). Phase 2 WebView→XAML is a separate doc.
- **[VERSION_MANAGEMENT_QUICK_REFERENCE.md](VERSION_MANAGEMENT_QUICK_REFERENCE.md)** - Quick answers to common version questions
- **[BEFORE_AFTER.md](BEFORE_AFTER.md)** - What changed from the old system to the new one

### For DevOps / CI-CD Engineers
- **[CI_CD_INTEGRATION.md](CI_CD_INTEGRATION.md)** - How versioning integrates with ci.yml, cd.yml, release.yml
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - System design, data flows, and visual diagrams

### For Complete Understanding
- **[VERSION_MANAGEMENT.md](VERSION_MANAGEMENT.md)** - Comprehensive reference guide with all details
- **[IMPLEMENTATION_COMPLETE.md](IMPLEMENTATION_COMPLETE.md)** - What was implemented and next steps

### For Setup & Organization
- **[HOW_TO_ADD_TO_SOLUTION.md](HOW_TO_ADD_TO_SOLUTION.md)** - How to add docs to the Visual Studio solution
- **[DOCUMENTATION.md](DOCUMENTATION.md)** - How documentation is organized

## 🎯 Start Here Based on Your Role

### I'm a Developer
**Goal:** Build and run VardyParty, understand versioning

**Read:**
1. [VERSION_MANAGEMENT_QUICK_REFERENCE.md](VERSION_MANAGEMENT_QUICK_REFERENCE.md) (5 min)
2. [BEFORE_AFTER.md](BEFORE_AFTER.md) (10 min)

**Key Takeaway:** Version management is automatic. Push code → CI/CD handles versions.

### I'm a DevOps / Pipeline Engineer
**Goal:** Understand how versions flow through CI/CD

**Read:**
1. [CI_CD_INTEGRATION.md](CI_CD_INTEGRATION.md) (15 min)
2. [ARCHITECTURE.md](ARCHITECTURE.md) (10 min)

**Key Takeaway:** Version.props is imported by both projects. cd.yml can optionally be updated to read from Version.props.

### I'm a Release Manager
**Goal:** Manage version bumps and releases

**Read:**
1. [VERSION_MANAGEMENT_QUICK_REFERENCE.md](VERSION_MANAGEMENT_QUICK_REFERENCE.md) → Common Tasks (5 min)
2. [CI_CD_INTEGRATION.md](CI_CD_INTEGRATION.md) → Release Pipeline (10 min)

**Key Takeaway:** Trigger "Bump Display Version" workflow in GitHub Actions. Everything else is automatic.

### I'm New to the Project
**Goal:** Understand the full system

**Read in Order:**
1. [IMPLEMENTATION_COMPLETE.md](IMPLEMENTATION_COMPLETE.md) (10 min)
2. [ARCHITECTURE.md](ARCHITECTURE.md) (15 min)
3. [VERSION_MANAGEMENT.md](VERSION_MANAGEMENT.md) (20 min)

**Key Takeaway:** Single Version.props file keeps both MAUI and Linux apps in sync. Workflows automate everything.

## 📖 Document Descriptions

| Document | Purpose | Audience | Time |
|----------|---------|----------|------|
| [VERSION_MANAGEMENT_QUICK_REFERENCE.md](VERSION_MANAGEMENT_QUICK_REFERENCE.md) | Quick answers, common tasks, troubleshooting | All | 5-10 min |
| [BEFORE_AFTER.md](BEFORE_AFTER.md) | What changed, benefits, comparison | Developers | 10-15 min |
| [CI_CD_INTEGRATION.md](CI_CD_INTEGRATION.md) | How it integrates with existing workflows | DevOps | 15-20 min |
| [ARCHITECTURE.md](ARCHITECTURE.md) | System design, data flows, diagrams | Architects | 20-30 min |
| [VERSION_MANAGEMENT.md](VERSION_MANAGEMENT.md) | Complete reference, all details | Everyone | 30-45 min |
| [IMPLEMENTATION_COMPLETE.md](IMPLEMENTATION_COMPLETE.md) | What was implemented, next steps | All | 10-15 min |
| [HOW_TO_ADD_TO_SOLUTION.md](HOW_TO_ADD_TO_SOLUTION.md) | How to add docs to the Visual Studio solution | Developers, DevOps | 5-10 min |
| [DOCUMENTATION.md](DOCUMENTATION.md) | How documentation is organized | All | 5 min |

## 🚀 Quick Start Commands

**Check current version:**
```bash
grep ApplicationDisplayVersion Version.props
grep ApplicationVersion Version.props
```

**Build locally with versioning:**
```bash
dotnet build VardyParty/VardyParty.csproj -c Release
```

**Verify DLL stamping:**
```powershell
$asm = [System.Reflection.AssemblyName]::GetAssemblyName("VardyParty.dll")
$asm.Version  # Shows: 1.7.2.0
```

**Bump version (GitHub Actions UI):**
1. Go to Actions
2. Click "Bump Display Version on Main Merge"
3. Run workflow
4. Select: major, minor, or patch

## 🔗 Key Files in Repository

| File | Purpose |
|------|---------|
| `Version.props` | Single source of truth for all versions |
| `VardyParty/VardyParty.csproj` | MAUI project (imports Version.props) |
| `VardyParty.Linux/VardyParty.Linux.csproj` | Linux project (imports Version.props) |
| `.github/workflows/auto-increment-build-version.yml` | Auto-increments build counter |
| `.github/workflows/bump-display-version.yml` | Manual semantic version bump |
| `.github/workflows/ci.yml` | Build & test (existing) |
| `.github/workflows/cd.yml` | Package & release (existing) |
| `.github/workflows/release.yml` | GitHub release (existing) |

## 📞 Getting Help

**Q: Where do I find X?**
- See "Quick Navigation" section above

**Q: How do I do Y?**
- Check [VERSION_MANAGEMENT_QUICK_REFERENCE.md](VERSION_MANAGEMENT_QUICK_REFERENCE.md) → Common Tasks

**Q: Why is Z not working?**
- Check [VERSION_MANAGEMENT.md](VERSION_MANAGEMENT.md) → Troubleshooting

**Q: How does this integrate with CI/CD?**
- Read [CI_CD_INTEGRATION.md](CI_CD_INTEGRATION.md)

## 📊 System Overview

```
Version.props (Single Source of Truth)
    ├─ ApplicationVersion: 35
    └─ ApplicationDisplayVersion: 1.7.2
         │
         ├─→ VardyParty.csproj (imports)
         │   └─ DLLs stamped: 1.7.2+35
         │
         ├─→ VardyParty.Linux.csproj (imports)
         │   └─ DLLs stamped: 1.7.2+35
         │
         └─→ GitHub Actions Workflows
             ├─ ci.yml: Builds with versions
             ├─ cd.yml: Packages with versions
             ├─ auto-increment: Bumps build counter
             └─ bump-display: Manual semantic version
```

## ✅ Implementation Status

- ✅ Version.props created
- ✅ Both projects importing Version.props
- ✅ Auto-increment workflow active
- ✅ Manual bump workflow active
- ✅ DLL stamping configured
- ✅ GitHub Actions integrated
- ✅ All documentation complete

## 🎯 Next Steps

1. **Verify system:** Push to feature branch → Check ci.yml runs
2. **Test packaging:** Merge to main → Check cd.yml creates release
3. **Test bumping:** Trigger bump-display-version → Create release
4. **Optional:** Update cd.yml to read from Version.props (see [CI_CD_INTEGRATION.md](CI_CD_INTEGRATION.md))

## 📝 Version History

All version changes are tracked in git commits:
- View with: `git log --oneline Version.props`
- Tags created for releases: `git tag -l`

## 🤝 Contributing

When making changes that affect versioning:
1. Don't edit Version.props manually (let automation handle it)
2. Version changes should go through GitHub Actions workflows
3. Follow the documented patterns in [CI_CD_INTEGRATION.md](CI_CD_INTEGRATION.md)

---

**Last Updated:** 2024
**Status:** ✅ Active
**Maintained by:** DevOps & Platform Engineering
