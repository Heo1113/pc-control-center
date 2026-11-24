<#
.SYNOPSIS
  GUI smoke test of the published EXE (adapted from project C's smoke-gui.ps1, commit 06cc9e5).
  1) mock-demo: MOCK sensors + MOCK demo data (--demo-data),
  2) real: this machine's real sensors (on a CI runner without an NVIDIA GPU: the "GPU not supported" path),
  3) after-kill: the app is started with a session recording (--start-test-session), left to sample, then killed
     (no shutdown code runs). gpc reads that data folder: samples written before the kill must be there, SQLite's
     integrity check must pass, the run must be unfinished and the session still open. Then the smoke test runs on the
     same folder: the app must recover the run and close the session at its last sample.
  Each smoke run drives every screen and the main commands, saves screenshots and a JSON report, and must exit with 0.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ReportDir = "dist/smoke",
    [int]$CollectSecondsBeforeKill = 30
)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
$cli = Join-Path (Split-Path $exePath) 'gpc.exe'
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
    $null = $process.Handle  # keeps the exit code available after exit
    if (-not $process.WaitForExit(480000)) {
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

$null = Invoke-Smoke 'mock-demo' (Join-Path $temp "gpc-gui-smoke-demo-$(Get-Random)") @('--mock-sensors', '--demo-data')
$null = Invoke-Smoke 'real' (Join-Path $temp "gpc-gui-smoke-real-$(Get-Random)") @()

# Abnormal end while a session is recording.
$killData = Join-Path $temp "gpc-gui-kill-$(Get-Random)"
$killOut = Join-Path $ReportDir 'after-kill'
New-Item -ItemType Directory -Force -Path $killOut | Out-Null
Write-Host "Abnormal end test: recording a session for $CollectSecondsBeforeKill s, then killing the app..."
$app = Start-Process -FilePath $exePath -ArgumentList @('--data-dir', $killData, '--mock-sensors', '--start-test-session') -PassThru
$null = $app.Handle
Start-Sleep -Seconds $CollectSecondsBeforeKill
if ($app.HasExited) {
    $failures.Add("after-kill: the app ended by itself before it was killed (exit code $($app.ExitCode))")
}
else {
    Stop-Process -Id $app.Id -Force
    $null = $app.WaitForExit(30000)
    $json = & $cli status --json --data-dir $killData
    if ($LASTEXITCODE -ne 0) {
        $failures.Add("after-kill: gpc status failed (exit code $LASTEXITCODE)")
    }
    else {
        $text = $json -join "`n"
        Set-Content -Path (Join-Path $killOut 'status-after-kill.json') -Value $text -Encoding utf8
        Write-Host $text
        $status = $text | ConvertFrom-Json
        if ($status.integrity -ne 'ok') { $failures.Add("after-kill: integrity check reported '$($status.integrity)'") }
        if ($status.database.GpuSamples -le 0) { $failures.Add('after-kill: no GPU samples kept from before the kill') }
        if (@($status.unfinishedRuns).Count -ne 1) { $failures.Add("after-kill: expected 1 unfinished run, found $(@($status.unfinishedRuns).Count)") }
        if (@($status.openSessions | Where-Object { $_.state -eq 'Running' }).Count -ne 1) { $failures.Add('after-kill: expected the test session to be still open') }
    }

    $after = Invoke-Smoke 'after-kill' $killData @('--mock-sensors')
    if ($after) {
        if (@($after.recoveredRuns).Count -ne 1) { $failures.Add("after-kill: expected 1 recovered run, found $(@($after.recoveredRuns).Count)") }
        if (@($after.recoveredSessions).Count -ne 1) { $failures.Add("after-kill: expected 1 recovered session, found $(@($after.recoveredSessions).Count)") }
    }
}

if ($failures.Count -gt 0) { throw "GUI smoke test failed: $($failures -join '; ')" }
Write-Host 'GUI smoke test OK (MOCK + demo data, real sensors, and recovery after the app was killed while recording).'
