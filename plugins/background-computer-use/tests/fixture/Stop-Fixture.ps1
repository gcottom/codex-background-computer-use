#requires -version 5.1
param(
    [Parameter(Mandatory = $true)][string]$RunDirectory,
    [ValidateRange(1, 15)][int]$TimeoutSeconds = 5
)
. (Join-Path $PSScriptRoot 'common.ps1')
$stoppedPath = Join-Path ([IO.Path]::GetFullPath($RunDirectory)) 'stopped.json'
if (Test-Path -LiteralPath $stoppedPath) { Get-Content -Raw -LiteralPath $stoppedPath; exit 0 }
$ready = Get-FixtureReady -RunDirectory $RunDirectory
$samples = New-Object 'System.Collections.Generic.List[object]'
$samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
[IO.File]::WriteAllText([string]$ready.stopRequestPath, 'explicit-fixture-stop')
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while (-not (Test-Path -LiteralPath $stoppedPath)) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Fixture stop timed out; no other process was killed. Automatic expiry remains active.' }
    $samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    Start-Sleep -Milliseconds 20
}
$samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
$result = [ordered]@{
    stopped = (Get-Content -Raw -LiteralPath $stoppedPath | ConvertFrom-Json)
    observation = Get-ObservationAssessment -Samples $samples.ToArray()
    samples = $samples.ToArray()
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunDirectory 'stop-observation.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 8
