# 📚 Documentation Quick Links Card

Print this or bookmark it for quick reference!

---

## 🏠 Documentation Hub

**Location:** `/docs` folder in repository root

**Also appears in:** Visual Studio Solution Explorer under "Documents" solution folder

---

## 🎯 Quick Navigation

### ⚡ **I Need a Quick Answer (5 min)**
→ Read: `docs/VERSION_MANAGEMENT_QUICK_REFERENCE.md`

### 🚀 **I'm Starting a Feature (10 min)**
→ Read: `docs/INDEX.md` + `.github/copilot-instructions.md`

### 🤖 **I'm an AI Agent (10-20 min)**
→ Read: `.github/AI_AGENT_INSTRUCTIONS.md`

### 🔧 **I'm a DevOps Engineer (20-30 min)**
→ Read: `docs/CI_CD_INTEGRATION.md` → `docs/ARCHITECTURE.md`

### 📦 **I'm Doing a Release (10 min)**
→ Read: `docs/VERSION_MANAGEMENT_QUICK_REFERENCE.md` → Common Tasks section

### 🆕 **I'm New to the Project (45 min)**
→ Read in order: `docs/INDEX.md` → `docs/IMPLEMENTATION_COMPLETE.md` → `docs/ARCHITECTURE.md` → Your role-specific docs

---

## 📂 File Locations

### In `/docs` Folder
```
docs/
├─ INDEX.md                          ← Navigation (START HERE)
├─ VERSION_MANAGEMENT.md             (Complete reference)
├─ VERSION_MANAGEMENT_QUICK_REFERENCE.md (Quick answers)
├─ CI_CD_INTEGRATION.md              (CI/CD details)
├─ ARCHITECTURE.md                   (System design)
├─ BEFORE_AFTER.md                   (What changed)
├─ IMPLEMENTATION_COMPLETE.md        (Status)
├─ DOCUMENTATION.md                  (Organization)
└─ ADDING_DOCS_TO_SOLUTION.md        (Solution setup)
```

### In `.github` Folder
```
.github/
├─ AI_AGENT_INSTRUCTIONS.md          (For AI agents)
├─ copilot-instructions.md           (Team standards)
└─ workflows/
   ├─ ci.yml                         (Build & Test)
   ├─ cd.yml                         (Package & Release)
   ├─ release.yml                    (GitHub Release)
   ├─ auto-increment-build-version.yml
   ├─ bump-display-version.yml
   └─ build.yml
```

### In Repository Root
```
Version.props                         (Version source of truth)
VardyParty.sln (or .slnx)           (Solution file)
```

---

## 🔑 Key Concepts

| Concept | File | Quick Answer |
|---------|------|--------------|
| **How do I check the version?** | `Version.props` | `grep ApplicationDisplayVersion Version.props` |
| **How do I bump the version?** | `docs/VERSION_MANAGEMENT_QUICK_REFERENCE.md` | Trigger GitHub Actions workflow |
| **How does CI/CD work?** | `docs/CI_CD_INTEGRATION.md` | Three stages: ci.yml → cd.yml → release.yml |
| **What changed?** | `docs/BEFORE_AFTER.md` | Centralized versions + auto-increment |
| **Where's the system design?** | `docs/ARCHITECTURE.md` | Visual diagrams + data flows |
| **How's the code organized?** | `.github/AI_AGENT_INSTRUCTIONS.md` | Multi-platform (MAUI + Linux) |
| **What are team standards?** | `.github/copilot-instructions.md` | Coding conventions + patterns |

---

## ⏱️ Reading Time Guide

| Document | Time | Best For |
|----------|------|----------|
| VERSION_MANAGEMENT_QUICK_REFERENCE.md | 5-10 min | Developers |
| INDEX.md | 5 min | Everyone |
| BEFORE_AFTER.md | 10-15 min | Understanding changes |
| CI_CD_INTEGRATION.md | 15-20 min | DevOps |
| ARCHITECTURE.md | 20-30 min | System design |
| VERSION_MANAGEMENT.md | 30-45 min | Complete reference |
| AI_AGENT_INSTRUCTIONS.md | 10-20 min | AI agents |
| copilot-instructions.md | 10-15 min | Code style |

---

## 🚀 Common Tasks

### Check Current Version
```bash
grep ApplicationDisplayVersion Version.props
```

### Build Locally
```bash
dotnet build VardyParty/VardyParty.csproj -c Release
```

### Bump Version (GitHub Actions)
1. Go to **Actions** tab
2. Click **Bump Display Version on Main Merge**
3. Run workflow
4. Select: major / minor / patch

### View Version in DLL
```powershell
$asm = [System.Reflection.AssemblyName]::GetAssemblyName("VardyParty.dll")
$asm.Version
```

---

## 📞 Need Help?

1. **Quick answer?** → `VERSION_MANAGEMENT_QUICK_REFERENCE.md` → Troubleshooting
2. **Lost?** → `docs/INDEX.md` → Find your role
3. **Deep dive?** → `docs/VERSION_MANAGEMENT.md` → Complete reference
4. **AI question?** → `.github/AI_AGENT_INSTRUCTIONS.md` → Full guide
5. **Code standards?** → `.github/copilot-instructions.md` → Team conventions

---

## ✨ Hot Tips

⭐ **Pro Tip #1:** Start with `docs/INDEX.md` - it has role-based guides
⭐ **Pro Tip #2:** Bookmark `docs/VERSION_MANAGEMENT_QUICK_REFERENCE.md` for daily use
⭐ **Pro Tip #3:** Search (Ctrl+F) in docs for specific topics
⭐ **Pro Tip #4:** AI agents should read `.github/AI_AGENT_INSTRUCTIONS.md` first
⭐ **Pro Tip #5:** Documentation is in Solution Explorer under "Documents" folder

---

## 📱 Mobile/Print Version

**For printing or mobile:** All documentation is also available in `/docs` folder as markdown files.

Save this as a bookmark in your browser or IDE!

---

**Last Updated:** 2024
**Status:** ✅ Complete
**Location:** `/docs` folder + `.github` folder
**For Quick Links:** This card
