param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$CertPasswordEnv = "MSIX_CERT_PASSWORD"
)

# Preflight: ensure dotnet SDK 10.x is available
$sdksLines = & dotnet --list-sdks 2>$null
$sdks = $sdksLines -join "`n"
if (-not ($sdksLines | Where-Object { $_ -match '^[\s\d]*10\.' })) {
    Write-Error "Required .NET 10 SDK not found on this machine. dotnet --list-sdks returned:`n$sdks"
    Write-Host 'Please install the .NET 10 SDK and MAUI workloads before attempting to publish an MSIX.'
    Write-Host 'Suggested steps:'
    Write-Host ' 1) Install .NET 10 SDK from https://dotnet.microsoft.com (select the appropriate preview/installer).'
    Write-Host ' 2) Install MAUI workloads: dotnet workload install microsoft-maui'
    Write-Host ' 3) Ensure Windows SDK & Windows App SDK are available for MSIX packaging (via Visual Studio Installer).'
    exit 2
}

$proj = "VardyParty/VardyParty.csproj"
$pwd = Get-Location

if (-not (Test-Path $proj)) { Write-Error "Project not found: $proj"; exit 1 }

if (-not (Test-Path "certs/vardyparty.pfx")) {
    Write-Host "Certificate not found at certs/vardyparty.pfx. Generating a self-signed cert (password P@ssw0rd!)."
    pwsh scripts/generate-selfsigned-cert.ps1 -OutPath certs/vardyparty.pfx -Password 'P@ssw0rd!'
    Write-Host "Setting MSIX_CERT_PASSWORD env var for this session"
    $env:MSIX_CERT_PASSWORD = 'P@ssw0rd!'
}

Write-Host "Publishing MSIX..."

# Resolve certificate password from environment
$certPassword = (Get-Item -Path "Env:\$CertPasswordEnv" -ErrorAction SilentlyContinue).Value
if (-not $certPassword) {
    Write-Warning "Environment variable $CertPasswordEnv is not set. Publish may fail if signing is required."
    $certPassword = ""
}

$certPath = Join-Path $pwd "certs\vardyparty.pfx"

# Build publish arguments
$publishCmd = @(
    'publish',
    $proj,
    '-c', $Configuration,
    '-r', $Runtime,
    '/p:WindowsPackageType=MSIX',
    '/p:AppxPackageSigningEnabled=true',
    "/p:PackageCertificateKeyFile=$certPath",
    "/p:PackageCertificatePassword=$certPassword",
    '--self-contained', 'false'
)

# Execute dotnet publish and capture output
Write-Host "Running: dotnet $($publishCmd -join ' ')"
$result = & dotnet @publishCmd 2>&1 | Out-String
$exitCode = $LASTEXITCODE

Write-Host "--- dotnet publish output ---"
Write-Host $result
if ($exitCode -ne 0) {
    Write-Host "Publish failed with exit code $exitCode"
    exit $exitCode
}

# Find generated msix
$searchRoot = Join-Path $pwd "VardyParty\bin\$Configuration\net10.0-windows10.0.19041.0\$Runtime\publish"
$search = Get-ChildItem -Path $searchRoot -Filter *.msix -Recurse -ErrorAction SilentlyContinue
if ($search) {
    $outDir = "artifacts"
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    foreach ($f in $search) { Copy-Item $f.FullName -Destination $outDir -Force }
    Write-Host "MSIX generated and copied to $outDir"
} else {
    Write-Host "No MSIX found in publish output. Check publish logs." 
}
