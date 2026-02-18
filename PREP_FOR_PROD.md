# Prepare for Production - CD Workflow Transition

## Overview
This document outlines the changes needed to transition the CD workflow from isolated testing on `feature/packaging` branch back to production where it automatically triggers after successful CI runs on `main` branch.

## Current State (Testing)
- **CI workflow**: Disabled on `feature/packaging` branch (exclude filter added)
- **CD workflow**: Triggers on push to `feature/packaging`
- **CD run-id**: Hardcoded to `66` for testing with existing artifacts
- **Purpose**: Isolated prototyping of `cd.yml` without triggering CI builds

## Production State (Target)
- **CI workflow**: Triggers normally on all branches (except excluded ones)
- **CD workflow**: Triggers automatically after successful CI on `main` branch
- **CD run-id**: Dynamic, uses `${{ github.event.workflow_run.id }}`
- **Purpose**: Automatic packaging and release after builds

## Changes Required

### 1. `.github/workflows/ci.yml` - Re-enable push trigger
**File**: `.github/workflows/ci.yml`
**Current** (line ~18):
```yaml
on:
  push:
    branches:
      - '**'
      - '!feature/packaging'  # ← REMOVE THIS LINE
  pull_request:
    branches:
      - main
  workflow_dispatch:
```

**Target**:
```yaml
on:
  push:
    branches:
      - '**'
  pull_request:
    branches:
      - main
  workflow_dispatch:
```

**Reason**: Remove the exclusion filter so CI runs on all branches including `feature/packaging` during testing on main.

---

### 2. `.github/workflows/cd.yml` - Change trigger and run-id
**File**: `.github/workflows/cd.yml`
**Current** (lines ~1-10):
```yaml
name: CD - Package & Release

env:
  DOTNET_VERSION: "10.0.x"
  VARDYPARTY_CSPROJ: "VardyParty/VardyParty.csproj"
  CI_RUN_ID: 66  # ← CHANGE THIS

on:
  push:
    branches:
      - feature/packaging  # ← CHANGE THIS
```

**Target**:
```yaml
name: CD - Package & Release

env:
  DOTNET_VERSION: "10.0.x"
  VARDYPARTY_CSPROJ: "VardyParty/VardyParty.csproj"

on:
  workflow_run:
    workflows: ["CI - Build & Test"]
    types: [completed]
    branches:
      - main
```

**Changes**:
- Replace `on.push` trigger with `on.workflow_run` trigger
- Remove `CI_RUN_ID: 66` environment variable
- Add workflow completion condition: `types: [completed]`
- Restrict to `main` branch only

---

### 3. Update all artifact download steps in `cd.yml`
**Pattern**: All download-artifact steps need to use dynamic run-id

**Current** (all jobs):
```yaml
run-id: ${{ env.CI_RUN_ID }}
```

**Target**:
```yaml
run-id: ${{ github.event.workflow_run.id }}
```

**Affected steps**:
- `package-windows`: Download Windows build artifact
- `package-android`: Download Android build artifact
- `package-ios`: Download iOS build artifact
- `package-macos`: Download macOS build artifact
- `package-linux-x64`: Download Linux x64 build artifact
- `package-linux-arm64`: Download Linux ARM64 build artifact

---

### 4. (Optional) Add success condition to CD jobs
**Enhancement**: Only run packaging if CI succeeded

**For each job**, add:
```yaml
jobs:
  package-windows:
    # ... existing config ...
    if: github.event.workflow_run.conclusion == 'success'
```

---

## Testing Checklist

- [ ] Verify `ci.yml` runs on feature/packaging without exclusion
- [ ] Merge `feature/packaging` to `main` once CD prototype is complete
- [ ] Verify CD trigger from workflow_run event on `main` branch
- [ ] Test artifact downloads use correct dynamic run-id
- [ ] Verify all platform packages are generated
- [ ] Check artifact retention periods (30 days for packages)

---

## Rollback Steps (if needed)

If issues arise during production transition:

1. **Revert CI trigger**: Add back `!feature/packaging` exclusion
2. **Revert CD trigger**: Change back to `push` to `feature/packaging`
3. **Revert run-id**: Change back to `CI_RUN_ID: 66` in env
4. **Investigation**: Check workflow logs on `main` branch

---

## Timeline

1. **Current**: Testing CD on `feature/packaging` with run-id 66
2. **Before merge**: Update all changes per this guide
3. **Merge**: Merge `feature/packaging` → `main`
4. **Verify**: Confirm CD triggers and completes successfully on next CI run
