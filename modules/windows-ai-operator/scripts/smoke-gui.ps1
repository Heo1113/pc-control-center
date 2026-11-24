<#
.SYNOPSIS
  GUI smoke test of the published EXE (same shape as the user's project B smoke-gui.ps1).
  1) mock: simulated desktop + MOCK model — every screen, the natural-language pipeline, plan review and a denied approval.
  2) real: the real Windows desktop (UI Automation) + MOCK model — a workflow and a natural-language request against the
     separate test app (OperatorTestTarget.exe from the package), and the window inspector.
  3) after-kill: a long task is started and the app is killed (no shutdown code runs). wao.exe must find exactly one
     unfinished task and a healthy database; the next start must close it as partially completed, never resume it.
  Each run saves screenshots and a JSON report and must exit with 0.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ReportDir = "dist/smoke",
    [int]$SecondsBeforeKill = 8
)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
$cli = Join-Path (Split-Path $exePath) 'wao.exe'
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$ReportDir = (Resolve-Path $ReportDir).Path
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Smoke([string]$Name, [string]$Data, [string[]]$Extra) {
    $out = Join-Path $ReportDir $Name
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $arguments = @('--smoke-test', '--data-dir', $Data, '--smoke-report', $out) + $Extra
    Write-Host "Running GUI smoke test ($Name)..."
    $process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(600000)) {
        Stop-Process -Id $process.Id -Force
        $failures.Add("${Name}: timed out")
        return $null
    }

    $report = Join-Path $out 'smoke-report.json'
    $parsed = $null
    if (Test-Path $report) {
        $raw = Get-Content $report -Raw -Encoding utf8
        Write-Host $raw
        $parsed = $raw | ConvertFrom-Json
    }
    else {
        $failures.Add("${Name}: no report written")
    }

    $logs = Join-Path $Data 'logs'
    if (Test-Path $logs) { Copy-Item -Recurse $logs (Join-Path $out 'logs') }
    if ($process.ExitCode -ne 0) { $failures.Add("${Name}: exit code $($process.ExitCode)") }
    return $parsed
}

$null = Invoke-Smoke 'mock' (Join-Path $temp "wao-smoke-mock-$(Get-Random)") @('--mock-desktop', '--mock-model')
$real = Invoke-Smoke 'real-uia' (Join-Path $temp "wao-smoke-real-$(Get-Random)") @('--mock-model', '--real-uia-smoke')
if ($real -and -not $real.realUia) { $failures.Add('real-uia: the run did not use the real desktop') }

# Abnormal end while a task is running.
$killData = Join-Path $temp "wao-smoke-kill-$(Get-Random)"
$killOut = Join-Path $ReportDir 'after-kill'
New-Item -ItemType Directory -Force -Path $killOut | Out-Null
Write-Host "Abnormal end test: starting a long task, killing the app after $SecondsBeforeKill s..."
$app = Start-Process -FilePath $exePath -ArgumentList @('--data-dir', $killData, '--start-test-task') -PassThru
$null = $app.Handle
Start-Sleep -Seconds $SecondsBeforeKill
if ($app.HasExited) {
    $failures.Add("after-kill: the app ended by itself before it was killed (exit code $($app.ExitCode))")
}
else {
    Stop-Process -Id $app.Id -Force
    $null = $app.WaitForExit(30000)
    $json = & $cli status --json --data-dir $killData
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("after-kill: wao status failed (exit code $LASTEXITCODE)")
    }
    else {
        $text = $json -join "`n"
        Set-Content -Path (Join-Path $killOut 'status-after-kill.json') -Value $text -Encoding utf8
        Write-Host $text
        $status = $text | ConvertFrom-Json
        if ($status.integrity -ne 'ok') { $failures.Add("after-kill: integrity check reported '$($status.integrity)'") }
        if (@($status.unfinishedTasks).Count -ne 1) { $failures.Add("after-kill: expected 1 unfinished task, found $(@($status.unfinishedTasks).Count)") }
    }

    $after = Invoke-Smoke 'after-kill' $killData @('--mock-desktop', '--mock-model')
    if ($after) {
        if (@($after.recoveredTasks).Count -ne 1) { $failures.Add("after-kill: expected 1 recovered task, found $(@($after.recoveredTasks).Count)") }
        elseif ($after.recoveredTasks[0].NewStatus -ne 'PartiallyCompleted') { $failures.Add("after-kill: recovered task status was $($after.recoveredTasks[0].NewStatus)") }
        if ($after.store.unfinished -ne 0) { $failures.Add('after-kill: unfinished tasks remain after recovery') }
    }
}

if ($failures.Count -gt 0) { throw "GUI smoke test failed: $($failures -join '; ')" }
Write-Host 'GUI smoke test OK (mock desktop + MOCK model, real UI Automation on the test app, recovery after the app was killed).'
