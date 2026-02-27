# Local Android Build with Secrets

This guide explains how to build the Android app locally with proper configuration secrets, matching the CI/CD pipeline behavior.

## Problem

The CI/CD pipeline merges secrets (Auth0, API endpoints) into `appsettings.json` during builds. When building locally, these secrets are missing, causing configuration errors.

## Solution

Use **User Secrets** + MSBuild target to patch `appsettings.json` during local Android builds.

## Setup (One-Time)

Set secrets manually using the .NET CLI:

```bash
# Initialize user secrets (if not already done)
dotnet user-secrets init --project .\VardyParty\VardyParty.csproj

# Set Auth0 configuration
dotnet user-secrets set "Auth0:Domain" "your-tenant.auth0.com" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:ClientId" "your-client-id" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:Audience" "your-audience" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:Scope" "openid profile email" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:CallbackScheme" "vardyparty" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:RedirectUri" "vardyparty://callback" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:PostLogoutRedirectUri" "vardyparty://callback" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:TokenLeewaySeconds" "60" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:RequiredRoleClaimType" "your-claim-type" --project .\VardyParty\VardyParty.csproj
dotnet user-secrets set "Auth0:RequiredRole" "your-required-role" --project .\VardyParty\VardyParty.csproj

# Set API configuration
dotnet user-secrets set "Api:HeadlessBaseUrl" "https://api.vardyparty.com" --project .\VardyParty\VardyParty.csproj
```

## Building Android with Secrets

Once secrets are configured, build Android with the `PatchAppSettings=true` parameter:

```bash
dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -p:PatchAppSettings=true
```

Or for Release builds:

```bash
dotnet build .\VardyParty\VardyParty.csproj -f net10.0-android -c Release -p:PatchAppSettings=true
```

## How It Works

1. **User Secrets Storage**: Secrets are stored in your user profile at:
   - Windows: `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`
   - Linux/Mac: `~/.microsoft/usersecrets/<UserSecretsId>/secrets.json`

2. **MSBuild Target**: When `PatchAppSettings=true` is set:
   - The `PatchAppSettingsForLocalAndroid` target runs **before the build starts**
   - It reads your user secrets from the secrets file
   - **Patches the SOURCE `appsettings.json` file directly**
   - Merges secrets into the source file
   - Removes `AllowUserSecrets` flag (like CI/CD does)
   - The build then embeds the patched file into the APK
   - Only affects Android builds (`net10.0-android`)
   - **⚠️ WARNING: This temporarily modifies your source file!**

3. **After Building**: 
   - Your source `appsettings.json` contains secrets
   - Revert it with: `git restore VardyParty\appsettings.json`
   - Or commit it to a local feature branch (never push secrets!)

## Verify Secrets

List all configured secrets:

```bash
dotnet user-secrets list --project .\VardyParty\VardyParty.csproj
```

## CI/CD Behavior

- **CI/CD**: Always patches appsettings.json from GitHub secrets
- **Local Builds**: Only patches when `-p:PatchAppSettings=true` is specified
- **Other Platforms**: Not affected (Windows, iOS, Mac)

## Troubleshooting

### Error: UserSecretsId not found

Make sure `VardyParty.csproj` has a `<UserSecretsId>` element. It should already exist.

### Error: User secrets file not found

Run the setup script or initialize user secrets:

```bash
dotnet user-secrets init --project .\VardyParty\VardyParty.csproj
```

### Secrets not applied

Make sure you're passing `-p:PatchAppSettings=true` to the build command.

### Wrong secrets in build

Check your secrets:

```bash
dotnet user-secrets list --project .\VardyParty\VardyParty.csproj
```

### Verify patched output

After building with `-p:PatchAppSettings=true`, check the patched source file:

```bash
cat .\VardyParty\appsettings.json
```

You should see your secrets merged in and `AllowUserSecrets` removed.

**Important:** Revert the source file after building:

```bash
git restore VardyParty\appsettings.json
```

## Cleaning Up

To remove all secrets:

```bash
dotnet user-secrets clear --project .\VardyParty\VardyParty.csproj
```

**After each build, revert the source appsettings.json:**

```bash
git restore VardyParty\appsettings.json
```

Build artifacts are cleaned with:

```bash
dotnet clean
```

## Important Notes

- **The source file is modified during build** - Always revert it with `git restore` after building
- **Never commit secrets** - Make sure to revert before committing
- **CI/CD is not affected** - This only runs when `-p:PatchAppSettings=true` is explicitly set
