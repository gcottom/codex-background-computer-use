param(
    [string]$DotnetPath = '',
    [string]$OutputPath = "$env:LOCALAPPDATA\BackgroundComputerUse\native",
    [string]$BuildRoot = "$env:LOCALAPPDATA\BackgroundComputerUse\build"
)
$ErrorActionPreference = 'Stop'
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw 'Install the pinned .NET SDK 10.0.403 or pass its absolute dotnet.exe path with -DotnetPath.'
    }
    $DotnetPath = $dotnetCommand.Source
}
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "dotnet.exe was not found: $DotnetPath"
}
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

