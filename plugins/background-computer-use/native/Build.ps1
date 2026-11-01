param(
    [string]$DotnetPath = 'D:\Tools\BackgroundComputerUse\dotnet\dotnet.exe',
    [string]$OutputPath = 'D:\Tools\BackgroundComputerUse\native',
    [string]$BuildRoot = 'D:\Tools\BackgroundComputerUse\build'
)
$ErrorActionPreference = 'Stop'
$toolRoot = Split-Path -Parent ([IO.Path]::GetFullPath($BuildRoot))
$env:DOTNET_CLI_HOME = Join-Path $toolRoot 'dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $toolRoot 'nuget-packages'
$project = Join-Path $PSScriptRoot 'BackgroundComputerUse.Native.csproj'
Push-Location $PSScriptRoot
try {
    & $DotnetPath restore $project --runtime win-x64 --locked-mode --artifacts-path $BuildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $DotnetPath publish $project --no-restore -c Release -o $OutputPath --artifacts-path $BuildRoot
    if ($LASTEXITCODE -ne 0) { throw 'Native helper publish failed.' }
    Get-FileHash -LiteralPath (Join-Path $OutputPath 'BackgroundComputerUse.Native.exe') -Algorithm SHA256
} finally { Pop-Location }

