#requires -version 5.1
# Opt-in diagnostic. Use the actual plugin interfaces separately for plugin acceptance.
param([ValidateRange(30, 120)][int]$LifetimeSeconds = 60, [string]$RunRoot = '')
$ErrorActionPreference = 'Stop'
$launch = (& (Join-Path $PSScriptRoot 'Start-Fixture.ps1') -LifetimeSeconds $LifetimeSeconds -RunRoot $RunRoot | ConvertFrom-Json)
$validation = $null
try {
    $validation = (& (Join-Path $PSScriptRoot 'Invoke-FixtureValidation.ps1') -RunDirectory $launch.runDirectory | ConvertFrom-Json)
} finally {
    $stopped = (& (Join-Path $PSScriptRoot 'Stop-Fixture.ps1') -RunDirectory $launch.runDirectory | ConvertFrom-Json)
}
[ordered]@{
    runDirectory = $launch.runDirectory
    launch = $launch.launchObservation
    validation = $validation
    stopped = $stopped
} | ConvertTo-Json -Depth 14
