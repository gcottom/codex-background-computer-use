#requires -version 5.1
param([string]$HelperPath = 'D:\Tools\BackgroundComputerUse\native\BackgroundComputerUse.Native.exe', [string]$RunRoot = '')
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not (Test-Path -LiteralPath $HelperPath -PathType Leaf)) { throw 'Native helper is not available at the requested path.' }
$launch = (& (Join-Path $PSScriptRoot 'Start-Fixture.ps1') -LifetimeSeconds 120 -RunRoot $RunRoot | ConvertFrom-Json)
$runDirectory = [string]$launch.runDirectory
$ready = Get-FixtureReady -RunDirectory $runDirectory
$records = New-Object 'System.Collections.Generic.List[object]'
$steps = New-Object 'System.Collections.Generic.List[object]'
$summary = [ordered]@{ schemaVersion = 1; runDirectory = $runDirectory; helperPath = $HelperPath; helperSha256 = (Get-FileHash -LiteralPath $HelperPath -Algorithm SHA256).Hash; launch = $launch.launchObservation; passed = $false }
function Invoke-NativeFixtureRequest {
    param([string]$Step, $Request, [string]$ExpectedError = '')
    $samples = New-Object 'System.Collections.Generic.List[object]'
    $samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $HelperPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine(($Request | ConvertTo-Json -Depth 10 -Compress))
    $process.StandardInput.Close()
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not $process.HasExited) {
        $samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
        if ([DateTime]::UtcNow -gt $deadline) { $process.Kill(); throw ('Native fixture step timed out: ' + $Step) }
        Start-Sleep -Milliseconds 10
    }
    $samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $result = $stdout | ConvertFrom-Json
    $observation = Get-ObservationAssessment -Samples $samples.ToArray()
    $record = [ordered]@{ step = $Step; request = $Request; exitCode = $process.ExitCode; result = $result; stderr = $stderr; observation = $observation; samples = $samples.ToArray() }
    $records.Add($record)
    $steps.Add([ordered]@{ step = $Step; exitCode = $process.ExitCode; error = $result.error.code; observation = $observation })
    $records.ToArray() | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $runDirectory 'native-e2e-steps.json') -Encoding UTF8
    if ($ExpectedError) {
        if ($process.ExitCode -eq 0 -or $result.error.code -ne $ExpectedError) { throw ('Unexpected rejection result for ' + $Step + ': ' + $stdout) }
    } elseif ($process.ExitCode -ne 0 -or $result.error) { throw ('Native fixture step failed: ' + $Step + ': ' + $stdout) }
    return $result
}
function New-FixtureRequest {
    param([string]$Verb)
    return @{ verb = $Verb; hwnd = [string]$ready.targetIdentity.hwnd; pid = [int]$ready.pid; start_time = [string]$ready.targetIdentity.start_time }
}
function Get-LiveFixtureReadback {
    return (Get-Content -Raw -LiteralPath $ready.readyPath | ConvertFrom-Json)
}
function Wait-FixtureCounter {
    param([int]$Expected)
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    do {
        $current = Get-LiveFixtureReadback
        if ([int]$current.counter -eq $Expected) { return $current }
        if ([DateTime]::UtcNow -gt $deadline) { throw ('Counter did not reach ' + $Expected) }
        Start-Sleep -Milliseconds 20
    } while ($true)
}
try {
    $inspectRequest = New-FixtureRequest 'inspect'
    $inspectRequest.depth = 6
    $inspectRequest.max_nodes = 300
    $inspect = Invoke-NativeFixtureRequest 'inspect-covered-target' $inspectRequest
    $valueElement = @($inspect.elements | Where-Object { $_.name -eq 'Test value' -and $_.control_type -eq 'Edit' })
    $buttonElement = @($inspect.elements | Where-Object { $_.name -eq 'Increment' -and $_.control_type -eq 'Button' })
    if ($valueElement.Count -ne 1 -or $buttonElement.Count -ne 1) { throw 'UIA did not find exactly one fixture Edit and Increment button.' }
    $valueElement = $valueElement[0]
    $buttonElement = $buttonElement[0]
    $stalePid = New-FixtureRequest 'inspect'; $stalePid.pid = [int]$ready.pid + 1
    Invoke-NativeFixtureRequest 'reject-stale-pid' $stalePid 'window_identity_mismatch' | Out-Null
    $staleStart = New-FixtureRequest 'inspect'; $staleStart.start_time = ([long]$ready.targetIdentity.start_time + 1).ToString()
    Invoke-NativeFixtureRequest 'reject-stale-start' $staleStart 'window_identity_mismatch' | Out-Null
    $setRequest = New-FixtureRequest 'act'
    $setRequest.expected_title = $inspect.window.title; $setRequest.expected_class_name = $inspect.window.class_name
    $setRequest.runtime_id = @($valueElement.runtime_id); $setRequest.expected = @{ name = $valueElement.name; automation_id = $valueElement.automation_id; control_type = $valueElement.control_type }
    $setRequest.action = 'set_value'; $setRequest.value = 'uia-background-verified'
    Invoke-NativeFixtureRequest 'uia-set-value-covered' $setRequest | Out-Null
    if ((Get-LiveFixtureReadback).value -ne $setRequest.value) { throw 'UIA value did not reach fixture readback.' }
    $invokeRequest = New-FixtureRequest 'act'
    $invokeRequest.expected_title = $inspect.window.title; $invokeRequest.expected_class_name = $inspect.window.class_name
    $invokeRequest.runtime_id = @($buttonElement.runtime_id); $invokeRequest.expected = @{ name = $buttonElement.name; automation_id = $buttonElement.automation_id; control_type = $buttonElement.control_type }
    $invokeRequest.action = 'invoke'
    Invoke-NativeFixtureRequest 'uia-invoke-covered' $invokeRequest | Out-Null
    Wait-FixtureCounter 1 | Out-Null
    $staleRuntime = @{} + $invokeRequest; $staleRuntime.runtime_id = @(-42)
    Invoke-NativeFixtureRequest 'reject-stale-runtime' $staleRuntime 'stale_element' | Out-Null
    $targetCapture = New-FixtureRequest 'capture'; $targetCapture.path = Join-Path $runDirectory 'wgc-target-occluded.png'; $targetCapture.timeout_ms = 4000
    $capture = Invoke-NativeFixtureRequest 'wgc-capture-covered-target' $targetCapture
    $coverCapture = @{ verb = 'capture'; hwnd = [string]$ready.coverIdentity.hwnd; pid = [int]$ready.pid; start_time = [string]$ready.coverIdentity.start_time; path = (Join-Path $runDirectory 'wgc-cover.png'); timeout_ms = 4000 }
    Invoke-NativeFixtureRequest 'wgc-capture-cover' $coverCapture | Out-Null
    $targetColors = [BackgroundComputerUseFixtureHarness.Native]::ImageStatistics($targetCapture.path)
    $coverColors = [BackgroundComputerUseFixtureHarness.Native]::ImageStatistics($coverCapture.path)
    $summary.targetColors = $targetColors; $summary.coverColors = $coverColors
    if ($capture.method -ne 'wgc-only' -or $targetColors.targetBackgroundPixels -lt 500 -or $targetColors.counterBackgroundPixels -lt 100 -or $targetColors.coverBackgroundPixels -ne 0 -or $coverColors.coverBackgroundPixels -lt 500 -or $coverColors.targetBackgroundPixels -ne 0) { throw 'WGC capture did not contain the expected independent target/cover painted pixels.' }
    $clickRequest = New-FixtureRequest 'click'
    $clickRequest.expected_title = $inspect.window.title; $clickRequest.expected_class_name = $inspect.window.class_name
    $clickRequest.expected_window_rect = $inspect.window.window_rect; $clickRequest.expected_client_rect = $inspect.window.client_rect
    $clickRequest.client_x = [int][Math]::Floor($buttonElement.bounds.x + $buttonElement.bounds.width / 2 - $inspect.window.client_rect.x)
    $clickRequest.client_y = [int][Math]::Floor($buttonElement.bounds.y + $buttonElement.bounds.height / 2 - $inspect.window.client_rect.y)
    $clickRequest.button = 'left'
    $click = Invoke-NativeFixtureRequest 'message-click-covered-increment' $clickRequest
    if (-not $click.messages_delivered) { throw 'Message-click did not report delivered messages.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(1)
    do {
        $afterClick = Get-LiveFixtureReadback
        if ([int]$afterClick.counter -eq 2 -or [DateTime]::UtcNow -gt $deadline) { break }
        Start-Sleep -Milliseconds 20
    } while ($true)
    $summary.messageClickCounterChanged = ([int]$afterClick.counter -eq 2)
    [IO.File]::WriteAllText([string]$ready.minimizeRequestPath, 'self-minimize-for-rejection-test')
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    while ((Get-LiveFixtureReadback).windowState -ne 'Minimized') {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Fixture did not self-minimize.' }
        Start-Sleep -Milliseconds 20
    }
    $minimizedCapture = New-FixtureRequest 'capture'; $minimizedCapture.path = Join-Path $runDirectory 'must-not-exist-minimized.png'; $minimizedCapture.timeout_ms = 4000
    Invoke-NativeFixtureRequest 'reject-minimized-capture' $minimizedCapture 'window_minimized' | Out-Null
    $minimizedWindow = [BackgroundComputerUseFixtureHarness.Native]::InspectWindow([string]$ready.targetHwnd, [int]$ready.pid)
    if (-not $minimizedWindow.minimized -or (Test-Path -LiteralPath $minimizedCapture.path)) { throw 'Minimized capture restored a window or wrote output.' }
    $summary.allStepsObservedForegroundPreserved = @($steps.ToArray() | Where-Object { -not $_.observation.foregroundPreserved }).Count -eq 0
    $summary.allStepsObservedCursorPreserved = @($steps.ToArray() | Where-Object { -not $_.observation.cursorPreserved }).Count -eq 0
    $summary.externalInputObserved = @($steps.ToArray() | Where-Object { $_.observation.externalInputObserved }).Count -gt 0
    $summary.passed = $summary.messageClickCounterChanged -and $summary.allStepsObservedForegroundPreserved -and $summary.allStepsObservedCursorPreserved -and -not $summary.externalInputObserved
    $summary.finalReadback = @{ counter = $afterClick.counter; value = $afterClick.value }
    $summary.minimizedRejectionPreservedMinimized = $true
} catch {
    $summary.error = ($_ | Out-String)
    $summary.error | Set-Content -LiteralPath (Join-Path $runDirectory 'native-e2e-error.txt') -Encoding UTF8
} finally {
    $summary.steps = $steps.ToArray()
    $summary.stop = (& (Join-Path $PSScriptRoot 'Stop-Fixture.ps1') -RunDirectory $runDirectory | ConvertFrom-Json)
    $summary | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $runDirectory 'native-e2e-summary.json') -Encoding UTF8
    $summary | ConvertTo-Json -Depth 14
}
if (-not $summary.passed) { exit 1 }
