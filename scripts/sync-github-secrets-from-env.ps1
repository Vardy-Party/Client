# Sync selected .env entries to GitHub Actions secrets for this repo.
# Requires: gh auth, .env in the repo root.
# Usage: pwsh scripts/sync-github-secrets-from-env.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root ".env"
if (-not (Test-Path $envFile)) {
    Write-Error "Missing $envFile — copy .env.example and fill values."
}

function Get-DotEnvValue([string]$key) {
    foreach ($line in Get-Content $envFile) {
        $trim = $line.Trim()
        if ($trim.StartsWith("#") -or $trim.Length -eq 0) { continue }
        $eq = $trim.IndexOf("=")
        if ($eq -lt 1) { continue }
        $name = $trim.Substring(0, $eq).Trim()
        if ($name -ne $key) { continue }
        return $trim.Substring($eq + 1).Trim().Trim('"').Trim("'")
    }
    return $null
}

function Set-SecretFromValue([string]$name, [string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        Write-Host "Skip $name (empty)"
        return
    }
    $value | gh secret set $name
    Write-Host "Set $name"
}

Set-SecretFromValue "WINDOWS_CERT_BASE64" (Get-DotEnvValue "WINDOWS_CERT_BASE64")
Set-SecretFromValue "WINDOWS_CERT_PASSWORD" (Get-DotEnvValue "WINDOWS_CERT_PASSWORD")
# Linux CD signs GitHub snaps with this; see package-linux-* in .github/workflows/cd.yml.
Set-SecretFromValue "MINISIGN_SECRET_KEY" (Get-DotEnvValue "MINISIGN_SECRET_KEY")
