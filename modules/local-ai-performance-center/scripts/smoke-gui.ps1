<#
.SYNOPSIS
  GUI smoke test of the published EXE: runs it twice (real sensors, then mock sensors) with --smoke-test.
  Each run drives every screen, records two short sessions, compares them, saves screenshots and a JSON
  report, and must exit with code 0.
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$ReportDir = "dist/smoke",
    [string]$Configuration = "Release",
    [int]$Port = 18081
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exePath = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$ReportDir = (Resolve-Path $ReportDir).Path
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$mockDll = Join-Path $root "tests/LocalAiPerf.MockStrata/bin/$Configuration/net10.0/LocalAiPerf.MockStrata.dll"
$mock = Start-Process -FilePath dotnet -ArgumentList @($mockDll, '--port', $Port) -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $temp 'mock-gui.out') -RedirectStandardError (Join-Path $temp 'mock-gui.err')
$failures = @()
try {
    Start-Sleep -Seconds 3
    foreach ($mode in @('real', 'mock')) {
        $out = Join-Path $ReportDir $mode
        New-Item -ItemType Directory -Force -Path $out | Out-Null
        $data = Join-Path $temp "lapc-gui-smoke-$mode-$(Get-Random)"
        $arguments = @('--smoke-test', '--no-auto-record', '--data-dir', $data, '--strata-url', "http://127.0.0.1:$Port", '--smoke-report', $out)
        if ($mode -eq 'mock') { $arguments += '--mock-sensors' }
        Write-Host "Running GUI smoke test ($mode sensors)..."
        $process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru
        $null = $process.Handle  # keeps the exit code available after exit
        if (-not $process.WaitForExit(240000)) {
            Stop-Process -Id $process.Id -Force
            $failures += "${mode}: timed out"
            continue
        }

        $report = Join-Path $out 'smoke-report.json'
        if (Test-Path $report) { Get-Content $report -Raw -Encoding utf8 | Write-Host } else { $failures += "${mode}: no report written" }
        $logs = Join-Path $data 'logs'
        if (Test-Path $logs) { Copy-Item -Recurse $logs (Join-Path $out 'logs') }
        if ($process.ExitCode -ne 0) { $failures += "${mode}: exit code $($process.ExitCode)" }
    }
}
finally {
    if ($mock -and -not $mock.HasExited) { Stop-Process -Id $mock.Id -Force }
}

if ($failures.Count -gt 0) { throw "GUI smoke test failed: $($failures -join '; ')" }
Write-Host 'GUI smoke test OK (real and mock sensors).'
