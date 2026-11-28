# Dot-source from packaging scripts. Builds a throwaway NuGet config for this process.
# Token comes only from the repo-root .env (VARDYPARTY_CLIENT_PACKAGES_TOKEN).
# Generic GitHub env vars are removed so another account on this machine cannot be used.

function Get-VardyPartyEnvValue {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$Key
    )
    $path = Join-Path $RepoRoot '.env'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $trim = $line.Trim()
        if ($trim.Length -eq 0 -or $trim.StartsWith('#')) { continue }
        if ($trim.StartsWith('export ')) { $trim = $trim.Substring(7).Trim() }
        $eq = $trim.IndexOf('=')
        if ($eq -lt 1) { continue }
        $name = $trim.Substring(0, $eq).Trim()
        if ($name -ne $Key) { continue }
        return $trim.Substring($eq + 1).Trim().Trim('"').Trim("'")
    }
    return $null
}

function Initialize-VardyPartyPackageFeed {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [ValidateSet('github', 'local', 'both')]
        [string]$Packages = 'github',
        [switch]$PackFromSource
    )

    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'never'
    $env:NUGET_CREDENTIALPROVIDER_FORCE_NONINTERACTIVE = 'true'
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN', 'NUGET_AUTH_TOKEN', 'NUGET_GITHUB_TOKEN', 'NUGET_GITHUB_USERNAME')) {
        Remove-Item -Path "Env:$name" -ErrorAction SilentlyContinue
    }
    Get-ChildItem Env: | Where-Object { $_.Name -like 'NuGetPackageSourceCredentials_*' } | ForEach-Object {
        Remove-Item -Path "Env:$($_.Name)" -ErrorAction SilentlyContinue
    }

    $useGitHub = $Packages -in @('github', 'both')
    $useLocal = $Packages -in @('local', 'both') -or $PackFromSource
    $repos = Split-Path -Parent $RepoRoot
    $localFeed = Join-Path $repos 'Strategies\artifacts\nuget'
    $token = $null
    $user = 'Vardy-Party'
    if ($useGitHub) {
        $token = Get-VardyPartyEnvValue -RepoRoot $RepoRoot -Key 'VARDYPARTY_CLIENT_PACKAGES_TOKEN'
        $fromFile = Get-VardyPartyEnvValue -RepoRoot $RepoRoot -Key 'VARDYPARTY_CLIENT_PACKAGES_USER'
        if (-not [string]::IsNullOrWhiteSpace($fromFile)) { $user = $fromFile }
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw @"
GitHub Packages needs VARDYPARTY_CLIENT_PACKAGES_TOKEN in the repo-root .env
(a Vardy-Party token with read:packages). Do not set GH_TOKEN or NUGET_GITHUB_TOKEN
for this script. Optional: VARDYPARTY_CLIENT_PACKAGES_USER=Vardy-Party.
Already-packed nupkgs: -Packages local
Pack Strategies and the resolver Client into the local feed first: -PackFromSource
"@
        }
    }

    if ($PackFromSource) {
        New-Item -ItemType Directory -Force -Path $localFeed | Out-Null
    }
    if ($useLocal -and -not $PackFromSource) {
        $nupkgs = @(Get-ChildItem -LiteralPath $localFeed -Filter '*.nupkg' -ErrorAction SilentlyContinue)
        if ($Packages -eq 'local' -and $nupkgs.Count -eq 0) {
            throw "No nupkgs in $localFeed. Pack them with -PackFromSource, or use -Packages github."
        }
        if ($nupkgs.Count -eq 0) { $useLocal = $false }
    }

    $sources = @(
        '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />'
    )
    $credentials = ''
    if ($useLocal) {
        $localUri = ([Uri]$localFeed).AbsoluteUri
        $sources += "    <add key=`"strategies-local`" value=`"$localUri`" />"
        Write-Host "Packages: local feed $localFeed"
    }
    if ($useGitHub) {
        $sources += '    <add key="github-vardy-party" value="https://nuget.pkg.github.com/Vardy-Party/index.json" />'
        $credentials = @"
  <packageSourceCredentials>
    <github-vardy-party>
      <add key="Username" value="$([System.Security.SecurityElement]::Escape($user))" />
      <add key="ClearTextPassword" value="$([System.Security.SecurityElement]::Escape($token))" />
    </github-vardy-party>
  </packageSourceCredentials>
"@
        Write-Host "Packages: GitHub Packages for Vardy-Party (token from .env, user $user)"
    }

    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
$($sources -join "`n")
  </packageSources>
$credentials
</configuration>
"@
    $config = Join-Path ([System.IO.Path]::GetTempPath()) ("vardyparty-nuget-" + [Guid]::NewGuid().ToString('n') + '.config')
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($config, $xml, $utf8)

    if ($PackFromSource) {
        $projects = @(
            (Join-Path $repos 'Strategies\VardyParty.LocalService.Abstractions\VardyParty.LocalService.Abstractions.csproj'),
            (Join-Path $repos 'Strategies\VardyParty.LocalService.V1\VardyParty.LocalService.V1.csproj'),
            (Join-Path $repos 'Strategies\VardyParty.LocalService.V2\VardyParty.LocalService.V2.csproj'),
            (Join-Path $repos 'M3U8-resolver\VardyParty.LocalService.Client\VardyParty.LocalService.Client.csproj')
        )
        Write-Host "Packing LocalService from source into $localFeed"
        Write-Host "NuGet uses a local nupkg only when its version matches the PackageReference."
        foreach ($project in $projects) {
            if (-not (Test-Path -LiteralPath $project)) {
                throw "Pack from source could not find $project"
            }
            $packLines = New-Object System.Collections.Generic.List[string]
            $packPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & dotnet pack $project -c Release -o $localFeed --configfile $config 2>&1 | ForEach-Object {
                    $line = "$_"
                    $packLines.Add($line)
                    Write-Host $line
                }
            }
            finally {
                $ErrorActionPreference = $packPreference
            }
            $packWarnings = @($packLines | Where-Object { $_ -match ': warning ' })
            if ($LASTEXITCODE -ne 0 -or $packWarnings.Count -gt 0) {
                Remove-Item -LiteralPath $config -Force -ErrorAction SilentlyContinue
                if ($packWarnings.Count -gt 0) {
                    throw "dotnet pack reported $($packWarnings.Count) warning(s) for $project"
                }
                throw "dotnet pack failed for $project"
            }
        }
    }

    return $config
}
