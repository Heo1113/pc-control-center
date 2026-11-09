<#
.SYNOPSIS
  GUI smoke test of the published PcControlCenter.exe (same shape as the original programs' smoke-gui.ps1).
  1) mock: MOCK sensors + labelled demo data of B and C + E's simulated desktop and MOCK model — every section and page,
     an A session, a B session, a C poll, an Operator natural-language task and a denied approval through the host process.
  2) real: the real sensor providers of the machine (on a CI runner: no NVIDIA GPU, so GPU values must be N/A, never
     invented), the real Windows event log, and E's built-in workflow on the real desktop against OperatorTestTarget.exe,
     executed by the separate Operator host process.
  3) after-kill: the app is killed while every module records; the next start must recover each module from its own
     database (A closes the open session, B closes the run and its interrupted sessions, C ends the run) and pass.
  Each run saves screenshots and a JSON report and must exit with 0.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ReportDir = "dist/smoke",
    [int]$SecondsBeforeKill = 15
)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path $Exe).Path
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
    if (-not $process.WaitForExit(900000)) {
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

    foreach ($logs in @((Join-Path $Data 'logs')) + (Get-ChildItem (Join-Path $Data 'modules') -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'logs' })) {
        if (Test-Path $logs) {
            $target = Join-Path $out ("logs-" + (Split-Path (Split-Path $logs) -Leaf))
            Copy-Item -Recurse $logs $target -ErrorAction SilentlyContinue
        }
    }
    if ($process.ExitCode -ne 0) { $failures.Add("${Name}: exit code $($process.ExitCode)") }
    return $parsed
}

$mockData = Join-Path $temp "pcc-smoke-mock-$(Get-Random)"
$mock = Invoke-Smoke 'mock' $mockData @('--mock-sensors', '--demo-data', '--mock-desktop', '--mock-model')
if ($mock -and -not $mock.sensor.isMock) { $failures.Add('mock: the run did not use mock sensors') }

# The real run also rehearses and imports B data written by the mock run (stands in for an original program's folder).
$real = Invoke-Smoke 'real' (Join-Path $temp "pcc-smoke-real-$(Get-Random)") @('--mock-model', '--smoke-migrate-from', (Join-Path $mockData 'modules\gpu-economics'))
if ($real -and -not $real.checks.'operator-real-uia-workflow') { $failures.Add('real: the Operator workflow on the real desktop did not complete') }
if ($real -and -not $real.checks.'migration-apply') { $failures.Add('real: the data import rehearsal/apply did not pass') }
if ($real -and $real.sensor.isMock) { $failures.Add('real: the run used mock sensors') }

# Abnormal end while every module records.
$killData = Join-Path $temp "pcc-smoke-kill-$(Get-Random)"
Write-Host "Abnormal end test: starting the app, killing it after $SecondsBeforeKill s..."
$app = Start-Process -FilePath $exePath -ArgumentList @('--data-dir', $killData, '--mock-sensors', '--mock-desktop') -PassThru
$null = $app.Handle
Start-Sleep -Seconds $SecondsBeforeKill
if ($app.HasExited) {
    $failures.Add("after-kill: the app ended by itself before it was killed (exit code $($app.ExitCode))")
}
else {
    Stop-Process -Id $app.Id -Force
    $null = $app.WaitForExit(30000)
    # The Operator host must not outlive the killed window (job object), and every module recovers from its own database.
    Start-Sleep -Seconds 3
    $orphans = Get-Process -Name 'PcControlCenter.OperatorHost' -ErrorAction SilentlyContinue
    if ($orphans) { $failures.Add("after-kill: $($orphans.Count) Operator host process(es) survived the killed window") }
    $after = Invoke-Smoke 'after-kill' $killData @('--mock-sensors', '--mock-desktop')
    if ($after -and -not $after.ok) { $failures.Add('after-kill: the restart after the kill did not pass') }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAILED: $_" }
    exit 1
}
Write-Host "GUI smoke tests passed."
