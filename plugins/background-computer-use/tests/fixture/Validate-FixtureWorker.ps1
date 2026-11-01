#requires -version 5.1
param([Parameter(Mandatory = $true)][string]$RunDirectory)
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    $ready = Get-FixtureReady -RunDirectory $RunDirectory
    $fixturePid = [int]$ready.pid
    $target = [BackgroundComputerUseFixtureHarness.Native]::InspectWindow([string]$ready.targetHwnd, $fixturePid)
    $cover = [BackgroundComputerUseFixtureHarness.Native]::InspectWindow([string]$ready.coverHwnd, $fixturePid)
    $sameBounds = $target.x -eq $cover.x -and $target.y -eq $cover.y -and $target.width -eq $cover.width -and $target.height -eq $cover.height
    $occlusionEstablished = $sameBounds -and $cover.ownerHwnd -eq $ready.targetHwnd -and $target.visible -and $cover.visible
    if (-not $occlusionEstablished -or -not $target.noActivate -or -not $cover.noActivate) { throw 'Fixture occlusion or non-activation invariant is not satisfied.' }
    $actionSamples = New-Object 'System.Collections.Generic.List[object]'
    $actionSamples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    $targetCapture = [BackgroundComputerUseFixtureHarness.Native]::Capture([string]$ready.targetHwnd, $fixturePid, (Join-Path $RunDirectory 'target-occluded.png'))
    $actionSamples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    $coverCapture = [BackgroundComputerUseFixtureHarness.Native]::Capture([string]$ready.coverHwnd, $fixturePid, (Join-Path $RunDirectory 'cover.png'))
    $actionSamples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    $expectedValue = 'background-validated-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $expectedCounter = [int]$ready.counter + 1
    [BackgroundComputerUseFixtureHarness.Native]::SetText([string]$ready.controls.testValue.hwnd, $fixturePid, $expectedValue)
    $actionSamples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    [BackgroundComputerUseFixtureHarness.Native]::Click([string]$ready.controls.increment.hwnd, $fixturePid)
    $actionSamples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    do {
        $readback = Get-Content -Raw -LiteralPath $ready.readyPath | ConvertFrom-Json
        if ($readback.value -eq $expectedValue -and [int]$readback.counter -eq $expectedCounter) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Fixture counter/value readback did not reach the expected state.' }
        Start-Sleep -Milliseconds 20
    } while ($true)
    $nativeValue = [BackgroundComputerUseFixtureHarness.Native]::Text([string]$ready.controls.testValue.hwnd, $fixturePid)
    $nativeCounter = [BackgroundComputerUseFixtureHarness.Native]::Text([string]$ready.controls.counter.hwnd, $fixturePid)
    $actionSamples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    $capturePassed = $targetCapture.printed -and $targetCapture.targetBackgroundPixels -gt 500 -and $targetCapture.counterBackgroundPixels -gt 100 -and $targetCapture.coverBackgroundPixels -eq 0 -and $coverCapture.printed -and $coverCapture.coverBackgroundPixels -gt 500 -and $coverCapture.coverStripePixels -gt 100 -and $coverCapture.targetBackgroundPixels -eq 0
    $semanticPassed = $nativeValue -eq $expectedValue -and $nativeCounter -eq ('Counter: ' + $expectedCounter)
    [ordered]@{
        schemaVersion = 1
        instanceId = $ready.instanceId
        pid = $fixturePid
        passed = [bool]($capturePassed -and $semanticPassed -and $occlusionEstablished)
        scope = 'Owned fixture diagnostic: native WM_SETTEXT/BM_CLICK and PrintWindow. This does not validate a UIA or external CLI plugin backend.'
        target = $target
        cover = $cover
        occlusionEstablished = [bool]$occlusionEstablished
        targetCapture = $targetCapture
        coverCapture = $coverCapture
        capturePassed = [bool]$capturePassed
        semanticPassed = [bool]$semanticPassed
        readback = @{ value = $nativeValue; counter = $nativeCounter; expectedValue = $expectedValue; expectedCounter = $expectedCounter }
        actionObservation = Get-ObservationAssessment -Samples $actionSamples.ToArray()
        actionSamples = $actionSamples.ToArray()
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $RunDirectory 'validation-result.json') -Encoding UTF8
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $RunDirectory 'validation-error.txt') -Encoding UTF8
    exit 1
}
