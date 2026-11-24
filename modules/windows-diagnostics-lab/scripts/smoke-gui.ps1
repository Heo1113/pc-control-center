<#
.SYNOPSIS
  GUI smoke test of the published EXE (adapted from project A).
  1) real-demo: real sensors + this machine's real event logs + the virtual demo scenario (--demo-data),
  2) mock: mock sensors + real event logs, no demo data,
  3) after-kill: the app is started normally, left to collect, then killed (no shutdown code runs). wdl reads that data
     folder: what was written before the kill must be there, SQLite's integrity check must pass and the killed session
     must be closed as an unclean end. Then the smoke test runs on the same folder (the app starts normally after a kill).
  Each smoke run drives every screen and the main commands, saves screenshots and a JSON report, and must exit with 0.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ReportDir = "dist/smoke",
    [int]$CollectSecondsBeforeKill = 40
)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
$cli = Join-Path (Split-Path $exePath) 'wdl.exe'
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$ReportDir = (Resolve-Path $ReportDir).Path
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Smoke([string]$Name, [string]$Data, [string[]]$Extra) {
    $out = Join-Path $ReportDir $Name
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $arguments = @('--smoke-test', '--data-dir', $Data, '--smoke-report', $out, '--history-days', '7') + $Extra
    Write-Host "Running GUI smoke test ($Name)..."
    $process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru
    $null = $process.Handle  # keeps the exit code available after exit
    if (-not $process.WaitForExit(420000)) {
        Stop-Process -Id $process.Id -Force
        $failures.Add("${Name}: timed out")
        return
    }

    $report = Join-Path $out 'smoke-report.json'
    if (Test-Path $report) { Get-Content $report -Raw -Encoding utf8 | Write-Host } else { $failures.Add("${Name}: no report written") }
    $logs = Join-Path $Data 'logs'
    if (Test-Path $logs) { Copy-Item -Recurse $logs (Join-Path $out 'logs') }
    if ($process.ExitCode -ne 0) { $failures.Add("${Name}: exit code $($process.ExitCode)") }
}

Invoke-Smoke 'real-demo' (Join-Path $temp "wdl-gui-smoke-real-$(Get-Random)") @('--demo-data')
Invoke-Smoke 'mock' (Join-Path $temp "wdl-gui-smoke-mock-$(Get-Random)") @('--mock-sensors')

# Abnormal end.
$killData = Join-Path $temp "wdl-gui-kill-$(Get-Random)"
$killOut = Join-Path $ReportDir 'after-kill'
New-Item -ItemType Directory -Force -Path $killOut | Out-Null
Write-Host "Abnormal end test: starting the app normally, collecting for $CollectSecondsBeforeKill s, then killing it..."
$app = Start-Process -FilePath $exePath -ArgumentList @('--data-dir', $killData, '--mock-sensors', '--history-days', '30') -PassThru
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
        $failures.Add("after-kill: wdl status failed (exit code $LASTEXITCODE)")
    }
    else {
        $text = $json -join "`n"
        Set-Content -Path (Join-Path $killOut 'status-after-kill.json') -Value $text -Encoding utf8
        Write-Host $text
        $status = $text | ConvertFrom-Json
        if ($status.integrity -ne 'ok') { $failures.Add("after-kill: integrity check reported '$($status.integrity)'") }
        if ($status.database.events -le 0) { $failures.Add('after-kill: no events kept from before the kill') }
        if ($status.database.sensorSamples -le 0) { $failures.Add('after-kill: no sensor samples kept from before the kill') }
        if (@($status.recoveredRuns).Count -ne 1) { $failures.Add("after-kill: expected 1 recovered session, found $(@($status.recoveredRuns).Count)") }
        if (-not $status.gapKinds.app_unclean_end) { $failures.Add('after-kill: the unclean end was not recorded as a gap') }
    }

    Invoke-Smoke 'after-kill' $killData @('--mock-sensors')
}

if ($failures.Count -gt 0) { throw "GUI smoke test failed: $($failures -join '; ')" }
Write-Host 'GUI smoke test OK (real logs with demo data, mock sensors, and a restart after the app was killed).'
