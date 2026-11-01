#requires -version 5.1
param(
    [Parameter(Mandatory = $true)][string]$RunDirectory,
    [Parameter(Mandatory = $true)][string]$InstanceId,
    [ValidateRange(15, 900)][int]$LifetimeSeconds = 180
)
$ErrorActionPreference = 'Stop'
try {
    Add-Type -Path (Join-Path $PSScriptRoot 'NativeFixture.cs') -ReferencedAssemblies System.Windows.Forms,System.Drawing,System.Web.Extensions
    [BackgroundComputerUseFixture.Program]::Run($RunDirectory, $InstanceId, $LifetimeSeconds)
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $RunDirectory 'error.txt') -Encoding UTF8
    exit 1
}
