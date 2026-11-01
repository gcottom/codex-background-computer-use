#requires -version 5.1
param(
    [Parameter(Mandatory = $true)][string]$RunDirectory,
    [ValidateRange(3, 30)][int]$TimeoutSeconds = 15
)
. (Join-Path $PSScriptRoot 'common.ps1')
$ready = Get-FixtureReady -RunDirectory $RunDirectory
$RunDirectory = [IO.Path]::GetFullPath($RunDirectory)
$resultPath = Join-Path $RunDirectory 'validation-result.json'
if (Test-Path -LiteralPath $resultPath) { throw 'This fixture run was already validated. Start a new fixture for a clean validation.' }
$samples = New-Object 'System.Collections.Generic.List[object]'
$samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$workerScript = Join-Path $PSScriptRoot 'Validate-FixtureWorker.ps1'
if ($workerScript.Contains('"') -or $RunDirectory.Contains('"')) { throw 'Unsupported double quote in fixture path.' }
$startInfo.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $workerScript + '" -RunDirectory "' + $RunDirectory + '"'
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.RedirectStandardInput = $true
$worker = [Diagnostics.Process]::Start($startInfo)
$worker.StandardInput.Close()
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$timedOut = $false
while (-not $worker.HasExited) {
    $samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
    if ([DateTime]::UtcNow -gt $deadline) {
        # Only this Process object, created above, is terminated. No HWND-owner process is killed.
        $timedOut = $true
        $worker.Kill()
        break
    }
    Start-Sleep -Milliseconds 10
}
$samples.Add([BackgroundComputerUseFixtureHarness.Native]::Snapshot())
$observation = [ordered]@{
    schemaVersion = 1
    instanceId = $ready.instanceId
    workerPid = $worker.Id
    timedOut = $timedOut
    observation = Get-ObservationAssessment -Samples $samples.ToArray()
    samples = $samples.ToArray()
}
$observation | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunDirectory 'validation-observation.json') -Encoding UTF8
if ($timedOut) { throw ('Bounded validation worker timed out. Fixture remains subject to its stop request and automatic expiry: ' + $RunDirectory) }
if ($worker.ExitCode -ne 0) { throw ('Fixture validation failed. See ' + (Join-Path $RunDirectory 'validation-error.txt')) }
$result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
[ordered]@{ validation = $result; observation = $observation.observation; observationPath = (Join-Path $RunDirectory 'validation-observation.json') } | ConvertTo-Json -Depth 12
if (-not $result.passed) { exit 1 }
