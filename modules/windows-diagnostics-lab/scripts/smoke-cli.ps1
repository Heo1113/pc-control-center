<#
.SYNOPSIS
  Command-line smoke test on this machine's real event logs: collect (history of 7 days), status, report, export,
  backup and verify, and an XML import into a separate data folder. Writes its outputs to the report folder.
#>
param(
    [string]$Configuration = "Release",
    [string]$ReportDir = "dist/smoke/cli"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$wdl = Join-Path $root "src/WinDiag.Cli/bin/$Configuration/net10.0/wdl.dll"
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$ReportDir = (Resolve-Path $ReportDir).Path
$temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$data = Join-Path $temp "wdl-cli-smoke-$(Get-Random)"

function Invoke-Wdl([string[]]$arguments) {
    Write-Host "> wdl $($arguments -join ' ')"
    & dotnet $wdl @arguments
    if ($LASTEXITCODE -ne 0) { throw "wdl $($arguments[0]) failed ($LASTEXITCODE)" }
}

Invoke-Wdl @('collect', '--days', '7', '--data-dir', $data)
Invoke-Wdl @('collect', '--data-dir', $data, '--no-snapshot')
Invoke-Wdl @('status', '--data-dir', $data)
Invoke-Wdl @('report', '--days', '7', '--data-dir', $data, '--out', (Join-Path $ReportDir 'report-real-logs.html'))
Invoke-Wdl @('export', '--days', '7', '--data-dir', $data, '--out', (Join-Path $ReportDir 'bundle'))
Invoke-Wdl @('backup', '--data-dir', $data, '--out', (Join-Path $ReportDir 'backup.db'))
Invoke-Wdl @('verify-backup', (Join-Path $ReportDir 'backup.db'))
$importData = Join-Path $temp "wdl-cli-import-$(Get-Random)"
Invoke-Wdl @('import-xml', (Join-Path $root 'tests/WinDiag.Tests/Fixtures/events/wevtutil-multi.xml'), '--data-dir', $importData)
Write-Host 'CLI smoke test OK.'
