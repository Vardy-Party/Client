param(
    [Parameter(Mandatory = $true)]
    [string]$Apk
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Apk)) {
    throw "APK not found: $Apk"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Apk))
try {
    $abis = @(
        $zip.Entries |
            Where-Object { $_.FullName -like 'lib/*/*.so' } |
            ForEach-Object { $_.FullName.Split('/')[1] } |
            Sort-Object -Unique
    )
}
finally {
    $zip.Dispose()
}

Write-Host "Native ABIs: $($abis -join ', ')"
if ($abis -notcontains 'armeabi-v7a' -or $abis -notcontains 'arm64-v8a') {
    throw "APK must contain armeabi-v7a (32-bit TV) and arm64-v8a (phones such as Nokia C12). Found: $($abis -join ', ')"
}
