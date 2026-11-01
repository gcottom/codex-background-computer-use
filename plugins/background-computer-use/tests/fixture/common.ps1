#requires -version 5.1
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1 (powershell.exe), not PowerShell 7, for this .NET Framework fixture harness.' }
if (-not ('BackgroundComputerUseFixtureHarness.Native' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'HarnessNative.cs') -ReferencedAssemblies System.Drawing
}
[BackgroundComputerUseFixtureHarness.Native]::UsePhysicalPixels()
function Get-FixtureReady {
    param([Parameter(Mandatory = $true)][string]$RunDirectory)
    $resolved = [IO.Path]::GetFullPath($RunDirectory)
    $ready = Get-Content -Raw -LiteralPath (Join-Path $resolved 'ready.json') | ConvertFrom-Json
    if ($ready.schemaVersion -ne 1 -or $ready.status -ne 'ready' -or $ready.instanceId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid fixture ready file.' }
    if ([IO.Path]::GetFullPath([string]$ready.readyPath) -ne (Join-Path $resolved 'ready.json')) { throw 'Fixture ready-path mismatch.' }
    if ([IO.Path]::GetFullPath([string]$ready.stopRequestPath) -ne (Join-Path $resolved 'stop.request')) { throw 'Fixture stop-path mismatch.' }
    $process = Get-Process -Id ([int]$ready.pid) -ErrorAction Stop
    $actualStarted = $process.StartTime.ToUniversalTime()
    $recordedStarted = [DateTime]::Parse([string]$ready.processStartedUtc).ToUniversalTime()
    if ($actualStarted.Ticks -ne $recordedStarted.Ticks) { throw 'Fixture PID was reused or the process identity changed.' }
    $command = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + [int]$ready.pid)
    if ($command.Name -ine 'powershell.exe' -or $command.CommandLine -notlike ('*' + $ready.instanceId + '*') -or $command.CommandLine -notlike '*run-fixture.ps1*') { throw 'The live process is not the expected fixture launcher.' }
    foreach ($handle in @($ready.targetHwnd, $ready.coverHwnd, $ready.controls.testValue.hwnd, $ready.controls.increment.hwnd, $ready.controls.counter.hwnd)) {
        [BackgroundComputerUseFixtureHarness.Native]::RequireOwnedWindow([string]$handle, [int]$ready.pid)
    }
    return $ready
}
function Get-ObservationAssessment {
    param([Parameter(Mandatory = $true)]$Samples)
    $items = @($Samples)
    if ($items.Count -lt 2) { throw 'At least two observation samples are required.' }
    $first = $items[0]
    $foregroundChanged = @($items | Where-Object { $_.foregroundHwnd -ne $first.foregroundHwnd }).Count -gt 0
    $cursorChanged = @($items | Where-Object { $_.cursorX -ne $first.cursorX -or $_.cursorY -ne $first.cursorY }).Count -gt 0
    $externalInputObserved = @($items | Where-Object { $_.lastInputTick -ne $first.lastInputTick }).Count -gt 0
    [ordered]@{
        sampleCount = $items.Count
        foregroundPreserved = -not $foregroundChanged
        cursorPreserved = -not $cursorChanged
        externalInputObserved = $externalInputObserved
        result = $(if ($foregroundChanged -or $cursorChanged -or $externalInputObserved) { 'inconclusive-state-or-external-input-changed' } else { 'observed-preserved' })
        attribution = 'Samples show observed state only. A change is not attributed to fixture code; user or other process activity may occur between samples.'
        before = $items[0]
        after = $items[$items.Count - 1]
    }
}
