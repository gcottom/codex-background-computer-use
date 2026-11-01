#requires -version 5.1
param(
    [ValidateRange(15, 900)][int]$LifetimeSeconds = 180,
    [ValidateRange(3, 30)][int]$ReadyTimeoutSeconds = 15,
    [string]$RunRoot = ''
)
. (Join-Path $PSScriptRoot 'common.ps1')
$instanceId = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $fixtureDataRoot = if ($env:BCU_DATA_DIR) { $env:BCU_DATA_DIR } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex\plugin-data\background-computer-use' }
    $RunRoot = Join-Path $fixtureDataRoot 'self-tests'
}
$RunRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RunRoot)
$runDirectory = Join-Path $RunRoot $instanceId
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$samples = New-Object 'System.Collections.Generic.List[object]'
$samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
$executable = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script = Join-Path $PSScriptRoot 'run-fixture.ps1'
if ($script.Contains('"') -or $runDirectory.Contains('"')) { throw 'Unsupported double quote in fixture path.' }
$childArguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -STA -File "' + $script + '" -RunDirectory "' + $runDirectory + '" -InstanceId ' + $instanceId + ' -LifetimeSeconds ' + $LifetimeSeconds
$child = [Diagnostics.Process]::GetProcessById([BackgroundComputerUseFixtureHarness.Native]::StartHiddenNoInheritedHandles($executable, $childArguments))
$deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
$readyPath = Join-Path $runDirectory 'ready.json'
while (-not (Test-Path -LiteralPath $readyPath)) {
    $samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    if ($child.HasExited) { throw ('Fixture exited before readiness. See ' + (Join-Path $runDirectory 'error.txt')) }
    if ([DateTime]::UtcNow -gt $deadline) {
        [IO.File]::WriteAllText((Join-Path $runDirectory 'stop.request'), 'readiness-timeout')
        throw ('Fixture did not become ready before the bounded timeout: ' + $runDirectory)
    }
    Start-Sleep -Milliseconds 20
}
$ready = Get-FixtureReady -RunDirectory $runDirectory
$samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
$result = [ordered]@{
    schemaVersion = 1
    runDirectory = $runDirectory
    readyPath = $readyPath
    instanceId = $instanceId
    pid = $ready.pid
    launchObservation = Get-ObservationAssessment -Samples $samples.ToArray()
    samples = $samples.ToArray()
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'launch-observation.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 8
