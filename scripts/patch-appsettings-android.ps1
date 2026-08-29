# Back-compat wrapper — Android MSBuild target still calls this name.
param(
    [Parameter(Mandatory = $true)][string]$AppSettingsPath,
    [Parameter(Mandatory = $true)][string]$UserSecretsId
)

& "$PSScriptRoot/patch-appsettings.ps1" -AppSettingsPath $AppSettingsPath -UserSecretsId $UserSecretsId
exit $LASTEXITCODE
